using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramTemporaryMessageCleaner : BackgroundService
{
    private readonly ITelegramMessageSender _sender;
    private readonly ILogger<TelegramTemporaryMessageCleaner> _logger;
    private sealed record ScheduledRestore(
        DateTime ExpiresAt,
        string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows,
        bool KeyboardOnly = false,
        string? PhotoFileId = null);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), ScheduledRestore> _scheduled = new();
    private sealed record PendingPrompt(string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows,
        string OriginalCaption, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> OriginalRows,
        ScheduledRestore Owner);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), PendingPrompt> _pendingPrompts = new();
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

    public void QueuePrompt(string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        string originalCaption, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> originalRows)
    {
        var owner = new ScheduledRestore(DateTime.UtcNow.AddSeconds(20), originalCaption, originalRows);
        _scheduled[(chatId, messageId)] = owner;
        _pendingPrompts[(chatId, messageId)] = new(caption, rows, originalCaption, originalRows, owner);
    }

    internal async Task PublishPendingPromptsAsync(CancellationToken ct)
    {
        foreach (var item in _pendingPrompts.ToArray())
        {
            var gate = _postGates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct)) continue;
            try
            {
                if (!_pendingPrompts.TryRemove(item.Key, out var pending)) continue;
                if (!_scheduled.TryGetValue(item.Key, out var owner) || !ReferenceEquals(owner, pending.Owner)) continue;
                var result = await _sender.EditPhotoCaptionAsync(item.Key.ChatId, item.Key.MessageId,
                    pending.Caption, pending.Rows, ct);
                _logger.LogInformation("Deferred reservation prompt chat={ChatId} post={PostId} successful={Successful} error={Error}",
                    item.Key.ChatId, item.Key.MessageId, result.IsSuccessful, result.Error);
                if ((result.IsSuccessful || result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true) &&
                    _scheduled.TryGetValue(item.Key, out var active) && ReferenceEquals(active, owner))
                {
                    // Refresh the CURRENT step once, never the initial volume keyboard.
                    // Keep callback payloads identical so no order action is replayed.
                    var refreshedRows = pending.Rows.Select(row => (IReadOnlyCollection<TelegramInlineButton>)row
                        .Select(button => button with { Text = button.Text + "\u2063" }).ToArray()).ToArray();
                    var refresh = await _sender.EditReplyMarkupAsync(item.Key.ChatId, item.Key.MessageId, refreshedRows, ct);
                    _logger.LogInformation("Reservation post refresh chat={ChatId} post={PostId} successful={Successful} error={Error}",
                        item.Key.ChatId, item.Key.MessageId, refresh.IsSuccessful, refresh.Error);
                }
                // Start the visible-step timeout after Telegram has responded.
                _scheduled.TryUpdate(item.Key, owner with { ExpiresAt = DateTime.UtcNow.AddSeconds(20) }, owner);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            { _logger.LogWarning(error, "Deferred reservation prompt failed"); }
            finally { gate.Release(); }
        }
    }

    public async Task<TelegramSendResult> UpdatePromptAsync(string chatId, long messageId,
        string prompt, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        string originalCaption, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> originalRows,
        TimeSpan delay, CancellationToken ct, bool keyboardOnly = false, string? photoFileId = null)
    {
        var gate = _postGates.GetOrAdd((chatId, messageId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // A caption-changing prior step must not leave stale confirmation details.
            var editKeyboardOnly = string.IsNullOrWhiteSpace(photoFileId) && keyboardOnly &&
                (!_scheduled.TryGetValue((chatId, messageId), out var previous) || previous.KeyboardOnly);
            Cancel(chatId, messageId);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = !string.IsNullOrWhiteSpace(photoFileId)
                ? await _sender.EditPhotoAsync(chatId, messageId, photoFileId, prompt, rows, ct)
                : editKeyboardOnly
                ? await _sender.EditReplyMarkupAsync(chatId, messageId, rows, ct)
                : await _sender.EditPhotoCaptionAsync(chatId, messageId, prompt, rows, ct);
            _logger.LogInformation("Reservation prompt edit chat={ChatId} post={MessageId} keyboardOnly={KeyboardOnly} elapsedMs={ElapsedMs} successful={Successful} error={Error}",
                chatId, messageId, editKeyboardOnly, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.IsSuccessful, result.Error);
            // Schedule recovery even after a transient edit failure.
            ScheduleRestore(chatId, messageId, originalCaption, originalRows, delay, editKeyboardOnly, photoFileId);
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
                var result = !string.IsNullOrWhiteSpace(current.PhotoFileId)
                    ? await _sender.EditPhotoAsync(item.Key.ChatId, item.Key.MessageId, current.PhotoFileId, current.Caption, current.Rows, ct)
                    : current.KeyboardOnly
                    ? await _sender.EditReplyMarkupAsync(item.Key.ChatId, item.Key.MessageId, current.Rows, ct)
                    : await _sender.EditPhotoCaptionAsync(item.Key.ChatId, item.Key.MessageId, current.Caption, current.Rows, ct);
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
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows, TimeSpan delay, bool keyboardOnly = false, string? photoFileId = null) =>
        _scheduled[(chatId, messageId)] = new(DateTime.UtcNow.Add(delay), caption, rows, keyboardOnly, photoFileId);

    public void Cancel(string chatId, long messageId)
    {
        _scheduled.TryRemove((chatId, messageId), out _);
        _pendingPrompts.TryRemove((chatId, messageId), out _);
    }

    public void ScheduleDelete(string chatId, long messageId, TimeSpan delay) =>
        _scheduledDeletes[(chatId, messageId)] = DateTime.UtcNow.Add(delay);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            foreach (var item in _interactionLocks.Where(item => item.Value.ExpiresAt <= now).ToArray())
                ((ICollection<KeyValuePair<(string, long), InteractionLock>>)_interactionLocks).Remove(item);
            await PublishPendingPromptsAsync(stoppingToken);
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
