using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramTemporaryMessageCleaner : BackgroundService
{
    private readonly ITelegramMessageSender _sender;
    private readonly ILogger<TelegramTemporaryMessageCleaner> _logger;
    private sealed record ScheduledRestore(
        DateTime ExpiresAt,
        string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), ScheduledRestore> _scheduled = new();
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), DateTime> _scheduledDeletes = new();
    private sealed record InteractionLock(long UserId, DateTime ExpiresAt);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), InteractionLock> _interactionLocks = new();

    public TelegramTemporaryMessageCleaner(
        ITelegramMessageSender sender,
        ILogger<TelegramTemporaryMessageCleaner> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public bool IsScheduled(string chatId, long messageId) => _scheduled.ContainsKey((chatId, messageId));

    public bool TryAcquireInteraction(
        string chatId, long messageId, long userId, TimeSpan timeout)
    {
        var key = (chatId, messageId);
        while (true)
        {
            var now = DateTime.UtcNow;
            if (!_interactionLocks.TryGetValue(key, out var current))
                return _interactionLocks.TryAdd(key, new(userId, now.Add(timeout))) ||
                    TryAcquireInteraction(chatId, messageId, userId, timeout);
            if (current.UserId == userId)
            {
                _interactionLocks[key] = current with { ExpiresAt = now.Add(timeout) };
                return true;
            }
            if (current.ExpiresAt > now) return false;
            if (_interactionLocks.TryUpdate(key, new(userId, now.Add(timeout)), current)) return true;
        }
    }

    public void ReleaseInteraction(string chatId, long messageId, long userId)
    {
        var key = (chatId, messageId);
        if (_interactionLocks.TryGetValue(key, out var current) && current.UserId == userId)
            _interactionLocks.TryRemove(key, out _);
    }

    public void ScheduleRestore(
        string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows, TimeSpan delay) =>
        _scheduled[(chatId, messageId)] = new(DateTime.UtcNow.Add(delay), caption, rows);

    public void Cancel(string chatId, long messageId) =>
        _scheduled.TryRemove((chatId, messageId), out _);

    public void ScheduleDelete(string chatId, long messageId, TimeSpan delay) =>
        _scheduledDeletes[(chatId, messageId)] = DateTime.UtcNow.Add(delay);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            foreach (var item in _interactionLocks.Where(item => item.Value.ExpiresAt <= now).ToArray())
                _interactionLocks.TryRemove(item.Key, out _);
            foreach (var item in _scheduled.Where(item => item.Value.ExpiresAt <= now).ToArray())
            {
                if (!_scheduled.TryRemove(item.Key, out _)) continue;
                var result = await _sender.EditPhotoCaptionAsync(
                    item.Key.ChatId, item.Key.MessageId, item.Value.Caption, item.Value.Rows, stoppingToken);
                if (!result.IsSuccessful)
                    _logger.LogWarning("Telegram reservation prompt restore failed: {Error}", result.Error);
            }
            foreach (var item in _scheduledDeletes.Where(item => item.Value <= now).ToArray())
            {
                if (!_scheduledDeletes.TryRemove(item.Key, out _)) continue;
                var result = await _sender.DeleteMessageAsync(
                    item.Key.ChatId, item.Key.MessageId, stoppingToken);
                if (!result.IsSuccessful)
                    _logger.LogWarning("Telegram temporary message delete failed: {Error}", result.Error);
            }
        }
    }
}
