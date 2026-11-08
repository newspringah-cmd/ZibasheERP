using Microsoft.Extensions.Options;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;
public sealed class ShippingTrackingNoticeWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ShippingTrackingNoticeWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Shipping tracking notice worker started; durable address replies enabled");
        while(!ct.IsCancellationRequested)
        {
            var delay=TimeSpan.FromSeconds(3.2);
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var sender=scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
                foreach(var job in await repository.GetPendingAsync(ShippingTrackingNotices.Channel,1,ct))
                {
                    try
                    {
                        await ShippingTrackingNotices.DeliverAsync(db,sender,options.Value,job,ct);
                        if(job.Status==NotificationOutboxStatus.Pending&&job.NextAttemptAt is DateTime after)
                            delay=TimeSpan.FromSeconds(Math.Max(3.2,(after-DateTime.UtcNow).TotalSeconds));
                    }
                    catch(Exception error)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(30);
                        job.LastError=error.GetType().Name;
                        logger.LogWarning("Shipping tracking notice interrupted for job {JobId}: {ErrorType}",job.Id,error.GetType().Name);
                    }
                    job.LockedUntil=null;await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception error)when(!ct.IsCancellationRequested){logger.LogWarning("Shipping tracking notice worker interrupted: {ErrorType}",error.GetType().Name);}
            await Task.Delay(delay,ct);
        }
    }
}
