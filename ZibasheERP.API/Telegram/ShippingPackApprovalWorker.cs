using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;
public sealed class ShippingPackApprovalWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ShippingPackApprovalWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Shipping pack approval worker started; durable photo delivery enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var jobs=await repository.GetPendingAsync(ShippingPackApproval.SendChannel,5,ct);
                foreach(var job in jobs)
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var retry=await controller.ProcessShippingPackApprovalSendAsync(job,ct);
                        job.Status=retry.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;
                        job.NextAttemptAt=retry.HasValue?DateTime.UtcNow.AddSeconds(retry.Value):null;
                        job.ProcessedAt=retry.HasValue?null:DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        // Reclaiming a job checks its persisted photo intents. An
                        // interrupted unknown HTTP result is never sent twice.
                        job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;
                        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));job.LastError=e.GetType().Name;
                        logger.LogWarning("Shipping pack approval queue interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Shipping pack approval worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
