using Microsoft.Extensions.Options;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
namespace ZibasheERP.API.Telegram;
public sealed class ShippingNameWarningWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<ShippingNameWarningWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled||string.IsNullOrWhiteSpace(options.Value.ShippingChatId))return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await ShippingNameWarnings.ReconcileAsync(db,options.Value,ct);
                var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();var sender=scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
                foreach(var job in await repository.GetPendingAsync(ShippingNameWarnings.Channel,10,ct))
                {
                    try{await ShippingNameWarnings.ProcessAsync(db,sender,options.Value,job,ct);}
                    catch(Exception error)when(!ct.IsCancellationRequested)
                    {job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,30*job.Attempts));job.LastError=error.GetType().Name;logger.LogWarning("Shipping name warning failed: {ErrorType}",error.GetType().Name);}
                    job.LockedUntil=null;await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception error)when(!ct.IsCancellationRequested){logger.LogWarning("Shipping name warning scan failed: {ErrorType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(5),ct);
        }
    }
}
