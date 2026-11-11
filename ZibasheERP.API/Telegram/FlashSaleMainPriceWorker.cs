using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;
public sealed class FlashSaleMainPriceWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<FlashSaleMainPriceWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach(var id in await FlashSaleMainPriceDisplay.TargetsAsync(db,DateTime.UtcNow,ct))
                {
                    var list=await db.SalesLists.AsNoTracking().SingleOrDefaultAsync(l=>l.Id==id&&!l.IsDeleted,ct);if(list is null||list.IsInventoryOffer)continue;
                    var key=FlashSaleMainPriceDisplay.Key(id);var job=await db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==key,ct);
                    var quote=await FlashSalePricing.QuoteAsync(db,list,DateTime.UtcNow,ct);var payload=JsonSerializer.Serialize(FlashSaleMainPriceDisplay.Snapshot(list,quote));
                    if(job is not null&&job.Payload==payload&&(job.Status==NotificationOutboxStatus.Processed||job.NextAttemptAt>DateTime.UtcNow))continue;
                    if(job is null){job=new(){Id=key,CustomerId=Guid.Empty,Channel=FlashSaleMainPriceDisplay.Channel,EventType="RefreshPromotionPrice",Recipient=list.Id.ToString("N"),CreatedAt=DateTime.UtcNow};db.NotificationOutbox.Add(job);}
                    job.Payload=payload;job.UpdatedAt=DateTime.UtcNow;
                    try
                    {
                        var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(scope.ServiceProvider);
                        await controller.RefreshFlashSaleMainPriceAsync(id,ct);
                        job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.NextAttemptAt=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(e is TelegramWebhookController.FlashSalePriceRetryException r?r.Seconds+1:60);
                        job.LastError=e.GetType().Name;logger.LogWarning("Promotion price refresh deferred: {ErrorType}",e.GetType().Name);
                    }
                    await db.SaveChangesAsync(ct);
                    if(job.Status==NotificationOutboxStatus.Pending)break;
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Promotion price scan failed: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(10),ct);
        }
    }
}
