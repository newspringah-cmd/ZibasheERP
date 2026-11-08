using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;
namespace ZibasheERP.API.Telegram;
public sealed class ReadyInventoryMaintenanceWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ReadyInventoryMaintenanceWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await InventoryReferencePricing.SynchronizeAsync(db,ct);
                await ReadyInventoryRemoval.RecoverAsync(db,ct);
                await ReadyInventoryRemoval.RecoverManualAlertsAsync(db,options.Value.InvoiceFailureChatId,ct);
                var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var restockReports=await repository.GetPendingAsync(InvoiceRestockTracking.Channel,5,ct);
                foreach(var job in restockReports)
                {
                    try
                    {
                        using var taskScope=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(taskScope.ServiceProvider);
                        await controller.ProcessInvoiceRestockTrackingAsync(job,ct);
                        job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(600,15*job.Attempts));job.LastError=e.Message=="RestockPublicationPending"?"منتظر انتشار موفق تمام اقلام در کانال موجودی":e.GetType().Name;}
                    await db.SaveChangesAsync(ct);
                }
                var cleanup=await repository.GetPendingAsync("InvoiceCancellationCleanup",5,ct);
                foreach(var job in cleanup)
                {
                    try
                    {
                        using var taskScope=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(taskScope.ServiceProvider);
                        await controller.ProcessInvoiceCancellationCleanupAsync(job,ct);
                        job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {var restricted=e.Message=="InvoiceMessageDeletionRestricted";job.Status=restricted?NotificationOutboxStatus.Failed:NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;job.NextAttemptAt=restricted?null:DateTime.UtcNow.AddSeconds(Math.Min(600,30*job.Attempts));job.LastError=restricted?"تلگرام اجازهٔ حذف این پیام را نمی‌دهد؛ حذف دستی لازم است.":e.GetType().Name;}
                    await db.SaveChangesAsync(ct);
                }
                var jobs=await repository.GetPendingAsync("ReadyInventoryRefresh",10,ct);
                foreach(var job in jobs)
                {
                    try
                    {
                        using var taskScope=scopes.CreateScope();
                        var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(taskScope.ServiceProvider);
                        await controller.RefreshReadyInventoryPostAsync(Guid.Parse(job.Recipient),ct);
                        job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.Attempts++;
                        var delay=e is InventoryPostRetryException throttled?throttled.RetryAfterSeconds:Math.Min(600,30*job.Attempts);
                        job.NextAttemptAt=DateTime.UtcNow.AddSeconds(delay);
                        job.LastError=e is InvalidOperationException&&e.Message.StartsWith("TelegramInventoryPost",StringComparison.Ordinal)?e.Message:e.GetType().Name;
                        if(job.Attempts>=3&&Guid.TryParse(job.Recipient,out var listId))
                            await ReadyInventoryRemoval.QueueManualAlertAsync(db,listId,options.Value.InvoiceFailureChatId,false,false,ct);
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning(e,"Ready inventory maintenance failed");}
            await Task.Delay(TimeSpan.FromSeconds(5),ct);
        }
    }
}
