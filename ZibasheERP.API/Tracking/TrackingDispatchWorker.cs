using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Tracking;

public sealed record TrackingDispatchJob(Guid BatchId, Guid DispatchId, long AdminChatId);

public sealed class TrackingDispatchRetryException(int seconds) : Exception("Tracking delivery temporarily rejected")
{
    public int Seconds { get; } = Math.Max(1, seconds);
}

public static class TrackingDispatchRetryPolicy
{
    public static bool HasUnknownDelivery(IEnumerable<TrackingDispatchDelivery> deliveries) =>
        deliveries.Any(value => !value.IsDeleted && value.AttemptedAt.HasValue && !value.SentAt.HasValue);

    public static DateTime NextAttempt(DateTime now, int attempts, int? retryAfter = null) =>
        now.AddSeconds(retryAfter.HasValue ? Math.Max(1, retryAfter.Value) + 1d : Math.Min(300, 5d * Math.Max(1, attempts)));

    public static bool CanResume(TrackingDispatch dispatch) =>
        !HasUnknownDelivery(dispatch.Deliveries) &&
        (dispatch.Status is TrackingDispatchStatus.Ready or TrackingDispatchStatus.Sending ||
         (dispatch.Status == TrackingDispatchStatus.Failed &&
          (dispatch.LastError?.StartsWith("خطای فنی", StringComparison.Ordinal) == true ||
           (dispatch.Deliveries.Count > 0 && dispatch.Deliveries.All(value => value.SentAt.HasValue)))));
}

/// <summary>SQL outbox leases survive restarts; every job covers one dispatch, never a whole HTTP request.</summary>
public sealed class TrackingDispatchWorker(IServiceScopeFactory scopes, IOptions<TelegramOptions> options,
    ILogger<TrackingDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.BotToken)) return;
        logger.LogInformation("Tracking dispatch worker started; durable queue recovery enabled");
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(3.1); // Source status edits share a group: stay below 20/minute.
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var repository = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
                var jobs = await repository.GetPendingAsync("TrackingDispatch", 1, ct);
                foreach (var job in jobs)
                {
                    try
                    {
                        var payload = JsonSerializer.Deserialize<TrackingDispatchJob>(job.Payload)
                            ?? throw new JsonException("Missing tracking job payload");
                        using var taskScope = scopes.CreateScope();
                        var taskDb = taskScope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var dispatch = await taskDb.TrackingDispatches.Include(value => value.Deliveries)
                            .FirstOrDefaultAsync(value => value.Id == payload.DispatchId &&
                                value.ImportBatchId == payload.BatchId && !value.IsDeleted, ct);
                        if (dispatch is null || dispatch.Status == TrackingDispatchStatus.Sent)
                        {
                            Complete(job);
                        }
                        else if (TrackingDispatchRetryPolicy.HasUnknownDelivery(dispatch.Deliveries))
                        {
                            dispatch.Status = TrackingDispatchStatus.Failed;
                            dispatch.LastError = "نتیجه تلاش قبلی نامشخص است؛ برای جلوگیری از ارسال تکراری نیازمند بررسی است";
                            dispatch.UpdatedAt = DateTime.UtcNow;
                            await taskDb.SaveChangesAsync(ct);
                            job.Status = NotificationOutboxStatus.Failed;
                            job.LastError = dispatch.LastError;
                        }
                        else if (dispatch.Status == TrackingDispatchStatus.NeedsReview)
                        {
                            job.Status = NotificationOutboxStatus.Failed;
                            job.LastError = "تطبیق گیرنده نیازمند بررسی است";
                        }
                        else
                        {
                            // A stopped process may have claimed the dispatch before attempting HTTP.
                            if (dispatch.Status == TrackingDispatchStatus.Sending)
                            {
                                dispatch.Status = TrackingDispatchStatus.Failed;
                                await taskDb.SaveChangesAsync(ct);
                            }
                            var controller = ActivatorUtilities.CreateInstance<TelegramWebhookController>(taskScope.ServiceProvider);
                            await controller.ProcessTrackingBatchAsync(payload.AdminChatId, payload.BatchId, ct,
                                payload.DispatchId, background: true);
                            var finished = await taskDb.TrackingDispatches.AsNoTracking().Include(value => value.Deliveries)
                                .SingleAsync(value => value.Id == payload.DispatchId, ct);
                            if (finished.Status == TrackingDispatchStatus.Sent) Complete(job);
                            else if (TrackingDispatchRetryPolicy.CanResume(finished))
                            {
                                job.Attempts++;
                                job.Status = NotificationOutboxStatus.Pending;
                                job.NextAttemptAt = TrackingDispatchRetryPolicy.NextAttempt(DateTime.UtcNow, job.Attempts);
                                job.LastError = "Delivery retained; safe completion retry scheduled";
                            }
                            else
                            {
                                job.Status = NotificationOutboxStatus.Failed;
                                job.LastError = "ارسال نیازمند بررسی است؛ جزئیات در گزارش سری ثبت شده است";
                            }
                        }
                    }
                    catch (TrackingDispatchRetryException error)
                    {
                        job.Attempts++;
                        job.Status = NotificationOutboxStatus.Pending;
                        job.NextAttemptAt = TrackingDispatchRetryPolicy.NextAttempt(DateTime.UtcNow, job.Attempts, error.Seconds);
                        job.LastError = "Telegram rate limit; retry scheduled";
                        logger.LogInformation("Tracking job {JobId} deferred for Telegram cooldown {Seconds} seconds", job.Id, error.Seconds);
                        delay = TimeSpan.FromSeconds((double)error.Seconds + 1);
                        // Persist the server's cooldown for other queued tracking cards as well.
                        await db.NotificationOutbox.Where(value => value.Channel == "TrackingDispatch" &&
                                value.Status == NotificationOutboxStatus.Pending &&
                                (!value.NextAttemptAt.HasValue || value.NextAttemptAt < job.NextAttemptAt))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.NextAttemptAt, job.NextAttemptAt), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        job.Attempts++;
                        job.Status = NotificationOutboxStatus.Pending;
                        job.NextAttemptAt = TrackingDispatchRetryPolicy.NextAttempt(DateTime.UtcNow, job.Attempts);
                        job.LastError = "Tracking processing interrupted; resume scheduled";
                        logger.LogWarning(error, "Tracking job {JobId} interrupted", job.Id);
                    }
                    job.LockedUntil = null;
                    job.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    if (job.Status == NotificationOutboxStatus.Processed)
                        logger.LogInformation("Tracking job {JobId} completed", job.Id);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogError(error, "Tracking outbox polling failed; pending jobs retained");
            }
            await Task.Delay(delay, ct);
        }
    }

    private static void Complete(NotificationOutbox job)
    {
        job.Status = NotificationOutboxStatus.Processed;
        job.ProcessedAt = DateTime.UtcNow;
        job.LastError = null;
        job.NextAttemptAt = null;
    }
}
