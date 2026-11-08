using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;
public sealed class ShippingPackFeedbackWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ShippingPackFeedbackWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Shipping pack feedback worker started; durable customer response enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                foreach(var job in await repository.GetPendingAsync(TelegramWebhookController.ShippingPackResponseChannel,10,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var retry=await controller.ProcessShippingPackResponseAsync(job,ct);
                        job.Status=retry.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;
                        job.NextAttemptAt=retry.HasValue?DateTime.UtcNow.AddSeconds(retry.Value):null;job.ProcessedAt=retry.HasValue?null:DateTime.UtcNow;
                        job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception error)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;
                        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));job.LastError=error.GetType().Name;
                        logger.LogWarning("Shipping pack feedback interrupted for job {JobId}: {ErrorType}",job.Id,error.GetType().Name);
                    }
                    await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception error)when(!ct.IsCancellationRequested){logger.LogWarning("Shipping pack feedback worker interrupted: {ErrorType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(2),ct);
        }
    }
}
