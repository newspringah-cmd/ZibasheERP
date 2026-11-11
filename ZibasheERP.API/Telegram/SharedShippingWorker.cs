using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public sealed class SharedShippingWorker(IServiceScopeFactory scopes,IOptions<TelegramOptions> options,ILogger<SharedShippingWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;
        logger.LogInformation("Shared shipping worker started; durable address refresh enabled");
        var nextPhotoControls=DateTime.MinValue;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var repository=scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                if(DateTime.UtcNow>=nextPhotoControls)
                {
                    using var photos=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(photos.ServiceProvider);
                    await controller.RecoverSharedMemberPhotoControlsAsync(ct);nextPhotoControls=DateTime.UtcNow.AddSeconds(15);
                }
                foreach(var job in await repository.GetPendingAsync("SharedShippingRefresh",5,ct))
                {
                    try
                    {
                        using var action=scopes.CreateScope();var controller=ActivatorUtilities.CreateInstance<TelegramWebhookController>(action.ServiceProvider);
                        await controller.RefreshSharedShippingAddressAsync(job,ct);
                        job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.NextAttemptAt=null;job.LastError=null;
                    }
                    catch(Exception e)when(!ct.IsCancellationRequested)
                    {
                        job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(300,15*job.Attempts));job.LastError=e.GetType().Name;
                        logger.LogWarning("Shared shipping refresh interrupted for job {JobId}: {ErrorType}",job.Id,e.GetType().Name);
                    }
                    job.LockedUntil=null;await repository.SaveChangesAsync(ct);
                }
            }
            catch(Exception e)when(!ct.IsCancellationRequested){logger.LogWarning("Shared shipping worker interrupted: {ErrorType}",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
        }
    }
}
