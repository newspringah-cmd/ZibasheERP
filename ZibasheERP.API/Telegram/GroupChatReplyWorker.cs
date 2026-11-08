using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public sealed class GroupChatReplyWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<GroupChatReplyWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Group chat reply worker started; durable administrator replies enabled");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                foreach(var job in await repository.GetPendingAsync(TelegramWebhookController.GroupChatReplyChannel,1,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        var delay=await controller.ProcessGroupChatReplyAsync(job,ct);
                        job.Status=delay.HasValue?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Processed;
                        job.NextAttemptAt=delay.HasValue?DateTime.UtcNow.AddSeconds(delay.Value):null;job.ProcessedAt=delay.HasValue?null:DateTime.UtcNow;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,15*job.Attempts));job.LastError=e.GetType().Name;
                        logger.LogWarning("Group chat reply interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    job.LockedUntil=null;await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Group chat reply worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
