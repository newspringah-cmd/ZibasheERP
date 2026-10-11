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
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), SemaphoreSlim> _postGates = new();
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

    public async Task<TelegramSendResult> UpdatePromptAsync(string chatId, long messageId,
        string prompt, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        string originalCaption, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> originalRows,
        TimeSpan delay, CancellationToken ct)
    {
        var gate = _postGates.GetOrAdd((chatId, messageId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            Cancel(chatId, messageId);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await _sender.EditPhotoCaptionAsync(chatId, messageId, prompt, rows, ct);
            _logger.LogInformation("Reservation prompt edit chat={ChatId} post={MessageId} elapsedMs={ElapsedMs} successful={Successful} error={Error}",
                chatId, messageId, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.IsSuccessful, result.Error);
            // Schedule recovery even after a transient edit failure.
            ScheduleRestore(chatId, messageId, originalCaption, originalRows, delay);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<TelegramSendResult> UpdatePostAsync(string chatId, long messageId,
        Func<Task<TelegramSendResult>> edit, CancellationToken ct)
    {
        var gate = _postGates.GetOrAdd((chatId, messageId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (IsScheduled(chatId, messageId))
                return new TelegramSendResult(false, "کاربر در حال انتخاب است؛ به‌روزرسانی لیست پس از این مرحله انجام می‌شود.");
            return await edit();
        }
        finally { gate.Release(); }
    }

    internal async Task RestoreDueAsync(DateTime now, CancellationToken ct)
    {
        foreach (var item in _scheduled.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            var gate = _postGates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct)) continue;
            try
            {
                if (!_scheduled.TryGetValue(item.Key, out var current) || !ReferenceEquals(current, item.Value)) continue;
                var result = await _sender.EditPhotoCaptionAsync(
                    item.Key.ChatId, item.Key.MessageId, current.Caption, current.Rows, ct);
                if (result.IsSuccessful || result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true)
                    ((ICollection<KeyValuePair<(string, long), ScheduledRestore>>)_scheduled).Remove(new(item.Key, current));
                else
                {
                    _logger.LogWarning("Telegram reservation prompt restore failed: {Error}", result.Error);
                    _scheduled.TryUpdate(item.Key, current with { ExpiresAt = DateTime.UtcNow.AddSeconds(5) }, current);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(error, "Telegram reservation prompt restore failed");
                _scheduled.TryUpdate(item.Key, item.Value with { ExpiresAt = DateTime.UtcNow.AddSeconds(5) }, item.Value);
            }
            finally { gate.Release(); }
        }
    }

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
                if (_interactionLocks.TryUpdate(key, current with { ExpiresAt = now.Add(timeout) }, current)) return true;
                continue;
            }
            if (current.ExpiresAt > now) return false;
            if (_interactionLocks.TryUpdate(key, new(userId, now.Add(timeout)), current)) return true;
        }
    }

    public void ReleaseInteraction(string chatId, long messageId, long userId)
    {
        var key = (chatId, messageId);
        if (_interactionLocks.TryGetValue(key, out var current) && current.UserId == userId)
            ((ICollection<KeyValuePair<(string, long), InteractionLock>>)_interactionLocks).Remove(new(key, current));
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
                ((ICollection<KeyValuePair<(string, long), InteractionLock>>)_interactionLocks).Remove(item);
            await RestoreDueAsync(now, stoppingToken);
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
