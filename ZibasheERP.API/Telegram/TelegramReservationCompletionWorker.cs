using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramReservationCompletionWorker(IServiceScopeFactory scopes,
    ILogger<TelegramReservationCompletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var repository = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var jobs = await repository.GetPendingAsync("ChannelReservation", 1, ct);
                foreach (var job in jobs)
                {
                    try
                    {
                        using var taskScope = scopes.CreateScope();
                        var controller = ActivatorUtilities.CreateInstance<TelegramWebhookController>(taskScope.ServiceProvider);
                        using var payload = JsonDocument.Parse(job.Payload);
                        await controller.FinishConfirmedReservationAsync(payload.RootElement.GetProperty("RequestId").GetGuid(), ct);
                        job.Status = NotificationOutboxStatus.Processed;
                        job.ProcessedAt = DateTime.UtcNow;
                        job.LastError = null;
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(error, "Reservation completion failed for {JobId}", job.Id);
                        job.Attempts++;
                        job.Status = NotificationOutboxStatus.Pending;
                        job.LastError = error.Message;
                        job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(300, 10 * job.Attempts));
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            { logger.LogError(error, "Reservation completion polling failed"); }
            await Task.Delay(1000, ct);
        }
    }
}
