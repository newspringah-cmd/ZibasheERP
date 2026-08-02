using System.Text.Json;
using Microsoft.Extensions.Options;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramMessageSender _sender;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramOutboxWorker> _logger;

    public TelegramOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ITelegramMessageSender sender,
        IOptions<TelegramOptions> options,
        ILogger<TelegramOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BotToken))
        {
            _logger.LogInformation("Telegram outbox worker is disabled.");
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
                _logger.LogError(exception, "Telegram outbox batch processing failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<INotificationOutboxRepository>();
        var pending = await repository.GetPendingAsync(
            Math.Clamp(_options.BatchSize, 1, 100),
            cancellationToken);

        foreach (var item in pending)
        {
            var notification = await repository.GetByIdAsync(item.Id, cancellationToken);
            if (notification is null || notification.Status != NotificationOutboxStatus.Pending)
                continue;

            TelegramSendResult result;
            try
            {
                var message = TelegramNotificationMessageFormatter.Format(
                    notification.EventType,
                    notification.Payload);
                result = await _sender.SendAsync(
                    notification.Recipient,
                    message,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                result = new TelegramSendResult(false, $"Invalid notification payload: {exception.Message}");
            }

            var now = DateTime.UtcNow;
            notification.Attempts++;
            notification.UpdatedAt = now;
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
                notification.LastError = Truncate(result.Error ?? "Telegram delivery failed.", 1000);
            }

            await repository.SaveChangesAsync(cancellationToken);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
