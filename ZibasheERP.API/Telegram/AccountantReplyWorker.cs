using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class AccountantReplyWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<AccountantReplyWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        var wasEnabled=false;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var enabled=await db.TelegramAccountantLearningSettings.AsNoTracking().Where(s=>!s.IsDeleted).OrderByDescending(s=>s.CreatedAt).Select(s=>s.IsAutoReplyEnabled).FirstOrDefaultAsync(ct);
                if(!enabled){wasEnabled=false;await Task.Delay(2000,ct);continue;}
                var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(scope.ServiceProvider);
                if(!wasEnabled)
                {var count=await controller.QueueStoredAccountantQuestionsAsync(ct);logger.LogInformation("Queued {Count} unanswered accountant questions from the last 24 hours.",count);wasEnabled=true;}
                var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var jobs=await repository.GetPendingAsync(TelegramWebhookController.AccountantReplyChannel,1,ct);
                if(jobs.Count==0)jobs=await repository.GetPendingAsync(TelegramWebhookController.AccountantBacklogChannel,1,ct);
                foreach(var job in jobs)
                    await controller.ProcessAccountantQuestionAsync(job,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch(Exception ex){logger.LogWarning(ex,"Accountant reply queue processing failed.");}
            await Task.Delay(1500,ct);
        }
    }
}
