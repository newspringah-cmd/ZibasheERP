using Microsoft.Extensions.Options;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.N8n;

public sealed class N8nOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IN8nWebhookSender _sender;
    private readonly N8nOptions _options;
    private readonly ILogger<N8nOutboxWorker> _logger;

    public N8nOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IN8nWebhookSender sender,
        IOptions<N8nOptions> options,
        ILogger<N8nOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("n8n outbox worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 2, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "n8n outbox batch processing failed.");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
        var pending = await repository.GetPendingAsync(
            "N8n",
            Math.Clamp(_options.BatchSize, 1, 100),
            cancellationToken);

        foreach (var notification in pending)
        {
            var result = await _sender.SendAsync(notification, cancellationToken);
            var now = DateTime.UtcNow;
            notification.Attempts++;
            notification.UpdatedAt = now;
            notification.LockedUntil = null;
            notification.NextAttemptAt = null;
            if (result.IsSuccessful)
            {
                notification.Status = NotificationOutboxStatus.Processed;
                notification.ProcessedAt = now;
                notification.LastError = null;
            }
            else
            {
                notification.Status = notification.Attempts >= Math.Max(1, _options.MaxAttempts)
                    ? NotificationOutboxStatus.Failed
                    : NotificationOutboxStatus.Pending;
                notification.LastError = Truncate(result.Error ?? "n8n delivery failed.", 1000);
                if (notification.Status == NotificationOutboxStatus.Pending)
                    notification.NextAttemptAt = now + NotificationRetryPolicy.DelayAfter(notification.Attempts);
            }
            await repository.SaveChangesAsync(cancellationToken);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
