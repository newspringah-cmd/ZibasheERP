using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;

public sealed class InvoiceTransferWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<InvoiceTransferWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Invoice transfer worker started; durable delivery and source cleanup enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                foreach(var job in await repository.GetPendingAsync(InvoiceTransferService.WorkChannel,5,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var retry=await controller.ProcessInvoiceTransferFollowupAsync(job,ct);
                        job.Status=retry.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;job.NextAttemptAt=retry.HasValue?DateTime.UtcNow.AddSeconds(retry.Value):null;
                        job.ProcessedAt=retry.HasValue?null:DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        var manual=e.Message=="InvoiceTransferManualCleanup";job.Status=manual?NotificationOutboxStatus.Failed:NotificationOutboxStatus.Pending;
                        job.LockedUntil=null;job.Attempts++;job.NextAttemptAt=manual?null:DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));
                        job.LastError=manual?"حذف دستی پیام قدیمی لازم است؛ از وضعیت انتقال دوباره تلاش کنید.":e.GetType().Name;
                        logger.LogWarning("Invoice transfer followup interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Invoice transfer worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
