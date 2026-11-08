using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public sealed class ShippingDecantReferenceWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ShippingDecantReferenceWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Shipping decant reference worker started; automatic photo reports enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                foreach(var job in await repository.GetPendingAsync(TelegramWebhookController.ShippingDecantStreamChannel,1,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var retry=await controller.ProcessShippingDecantStreamAsync(job,ct);
                        job.Status=retry.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;
                        job.NextAttemptAt=retry.HasValue?DateTime.UtcNow.AddSeconds(retry.Value):null;
                        job.ProcessedAt=retry.HasValue?null:DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;
                        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));job.LastError=e.GetType().Name;
                        logger.LogWarning("Shipping decant reference queue interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Shipping decant reference worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
