using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class PaidPaymentTrackingCleanupWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,
    ILogger<PaidPaymentTrackingCleanupWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var chat=options.Value.NewPaymentsChatId.Trim();
        if(!options.Value.Enabled||string.IsNullOrWhiteSpace(chat))return;
        while(!ct.IsCancellationRequested)
        {
            var delay=TimeSpan.FromSeconds(30);
            try
            {
                using var scope=scopes.CreateScope();
                var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var service=new PaidPaymentTrackingCleanup(db,scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>());
                var deferred=(await db.NotificationOutbox.AsNoTracking().Where(n=>n.Channel==PaidPaymentTrackingCleanup.Channel&&n.NextAttemptAt>DateTime.UtcNow).Select(n=>n.Id).ToArrayAsync(ct)).ToHashSet();
                foreach(var target in (await service.CandidatesAsync(chat,ct)).Where(t=>!deferred.Contains(PaidPaymentTrackingCleanup.Key(t))))
                {
                    try
                    {
                        await service.ProcessAsync(target,chat,ct);
                        var id=PaidPaymentTrackingCleanup.Key(target);
                        var job=await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct);
                        if(job?.Status==NotificationOutboxStatus.Pending&&job.NextAttemptAt>DateTime.UtcNow)
                        {delay=job.NextAttemptAt.Value-DateTime.UtcNow+TimeSpan.FromSeconds(1);break;}
                        await Task.Delay(TimeSpan.FromMilliseconds(500),ct);
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {logger.LogWarning("Paid list report cleanup deferred for {ListId}: {ErrorType}",target.SalesListId,e.GetType().Name);}
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Paid list report scan interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(delay,ct);
        }
    }
}
