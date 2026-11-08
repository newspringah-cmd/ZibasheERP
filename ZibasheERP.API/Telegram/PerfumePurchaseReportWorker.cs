using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public sealed class PerfumePurchaseReportWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<PerfumePurchaseReportWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Perfume purchase report worker started; owner-only reports enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                foreach(var job in await repository.GetPendingAsync(TelegramWebhookController.PerfumePurchaseReportChannel,1,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var retry=await controller.ProcessPerfumePurchaseReportAsync(job,ct);
                        job.Status=retry.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;
                        job.NextAttemptAt=retry.HasValue?DateTime.UtcNow.AddSeconds(retry.Value):null;
                        job.ProcessedAt=retry.HasValue?null:DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;
                        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));job.LastError=e.GetType().Name;
                        logger.LogWarning("Perfume purchase report queue interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Perfume purchase report worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
