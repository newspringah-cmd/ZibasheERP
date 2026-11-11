using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class ManualShippingAddressWorker(IServiceScopeFactory scopes, IOptions<TelegramOptions> options,
    ILogger<ManualShippingAddressWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.ShippingChatId)) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sender = scope.ServiceProvider.GetRequiredService<ITelegramMessageSender>();
                var controller = ActivatorUtilities.CreateInstance<TelegramWebhookController>(scope.ServiceProvider);
                var sources = await ManualShippingAddresses.SourcesAsync(db, options.Value, ct);
                foreach (var source in sources)
                {
                    try
                    {
                        var sent = await ManualShippingAddresses.IsSentAsync(db, source, ct);
                        if (!sent && await ManualShippingAddresses.HasTrackingProofAsync(db, source, sources, options.Value, ct))
                        { await ManualShippingAddresses.CompleteAsync(db, source, null, "acknowledged-tracking-delivery", ct); sent = true; }
                        var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("manual-address-controls:" + source.ReferenceId.ToString("N")))[..16]);
                        var job = await db.NotificationOutbox.SingleOrDefaultAsync(n => n.Id == id, ct);
                        var signature = sent ? "sent-v1" : "open-v1";
                        if (job is not null && job.Payload == signature && (job.Status == NotificationOutboxStatus.Processed || job.NextAttemptAt > DateTime.UtcNow)) continue;
                        if (job is null)
                        {
                            job = new() { Id = id, CustomerId = source.Root.CustomerId, Channel = ManualShippingAddresses.ControlsChannel,
                                EventType = "ManualAddressControls", Recipient = source.Root.ChatId, CreatedAt = DateTime.UtcNow };
                            db.NotificationOutbox.Add(job);
                        }
                        var rows = await controller.ManualAddressButtonsAsync(source, sent, ct);
                        var result = await sender.EditReplyMarkupAsync(source.Root.ChatId, source.Root.MessageId!.Value, rows, ct);
                        job.Payload = signature; job.UpdatedAt = DateTime.UtcNow;
                        if (result.IsSuccessful || result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Error?.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) == true)
                        { job.Status = NotificationOutboxStatus.Processed; job.ProcessedAt = DateTime.UtcNow; job.NextAttemptAt = null; job.LastError = null; }
                        else
                        { job.Status = NotificationOutboxStatus.Pending; job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Max(30, result.RetryAfterSeconds ?? 30) + 1); job.LastError = "Manual address controls require retry"; }
                        await db.SaveChangesAsync(ct);
                        if (result.IsRetryable) break;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    { logger.LogWarning("Manual address refresh failed: {ErrorType}", e.GetType().Name); db.ChangeTracker.Clear(); }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { logger.LogWarning("Manual address scan failed: {ErrorType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
}
