using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramTemporaryMessageCleaner : BackgroundService
{
    private readonly ITelegramMessageSender _sender;
    private readonly ILogger<TelegramTemporaryMessageCleaner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _promptSlots = new(8, 8);
    private readonly SemaphoreSlim _maintenanceSlots = new(4, 4);
    private readonly SemaphoreSlim _workAvailable = new(0, 1);
    private const int MaxPromptAttempts = 3;
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(20);
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
    private sealed record ScheduledRestore(
        DateTime ExpiresAt,
        string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows,
        bool KeyboardOnly = false,
        string? PhotoFileId = null);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), ScheduledRestore> _scheduled = new();
    private sealed record PendingPrompt(string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows,
        ScheduledRestore Owner, DateTime NextAttemptAt, long? UserId, string? CallbackQueryId,
        string? SuccessMessage, int Attempts = 0);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), PendingPrompt> _pendingPrompts = new();
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), SemaphoreSlim> _postGates = new();
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), DateTime> _scheduledDeletes = new();
    private sealed record InteractionLock(long UserId, DateTime ExpiresAt);
    private readonly ConcurrentDictionary<(string ChatId, long MessageId), InteractionLock> _interactionLocks = new();

    public TelegramTemporaryMessageCleaner(
        ITelegramMessageSender sender,
        ILogger<TelegramTemporaryMessageCleaner> logger,
        TimeProvider? timeProvider = null)
    {
        _sender = sender;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsScheduled(string chatId, long messageId) => _scheduled.ContainsKey((chatId, messageId));

    public void QueuePrompt(string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        string originalCaption, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> originalRows,
        string? photoFileId = null, string? callbackQueryId = null, bool keyboardOnly = false,
        string? successMessage = null)
    {
        var now = UtcNow;
        var owner = new ScheduledRestore(now.Add(SelectionTimeout), originalCaption, originalRows,
            KeyboardOnly: keyboardOnly, PhotoFileId: keyboardOnly ? null : photoFileId);
        _interactionLocks.TryGetValue((chatId, messageId), out var interaction);
        _scheduled[(chatId, messageId)] = owner;
        _pendingPrompts[(chatId, messageId)] = new(caption, rows, owner, now, interaction?.UserId, callbackQueryId, successMessage);
        WakeWorker();
    }

    internal Task PublishPendingPromptsAsync(CancellationToken ct) =>
        Task.WhenAll(StartPendingPrompts(ct));

    private IEnumerable<Task> StartPendingPrompts(CancellationToken ct)
    {
        foreach (var item in _pendingPrompts.ToArray())
        {
            if (item.Value.NextAttemptAt > UtcNow || !_promptSlots.Wait(0)) continue;
            var gate = _postGates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                _promptSlots.Release();
                continue;
            }
            yield return PublishPromptAsync(item.Key, item.Value, gate, ct);
        }
    }

    private async Task PublishPromptAsync((string ChatId, long MessageId) key, PendingPrompt pending,
        SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            if (!_pendingPrompts.TryGetValue(key, out var current) || !ReferenceEquals(current, pending)) return;
            if (!_scheduled.TryGetValue(key, out var owner) || !ReferenceEquals(owner, pending.Owner) || owner.ExpiresAt <= UtcNow)
            {
                RemovePending(key, pending);
                return;
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = owner.KeyboardOnly
                ? await _sender.EditReplyMarkupAsync(key.ChatId, key.MessageId, pending.Rows, ct)
                : !string.IsNullOrWhiteSpace(owner.PhotoFileId)
                ? await _sender.EditPhotoAsync(key.ChatId, key.MessageId, owner.PhotoFileId, pending.Caption, pending.Rows, ct)
                : await _sender.EditPhotoCaptionAsync(key.ChatId, key.MessageId, pending.Caption, pending.Rows, ct);
            _logger.LogInformation("Deferred reservation prompt chat={ChatId} post={PostId} successful={Successful} error={Error} attempt={Attempt} queuedMs={QueuedMs} editMs={EditMs}",
                key.ChatId, key.MessageId, result.IsSuccessful, result.Error, pending.Attempts + 1,
                (UtcNow - (owner.ExpiresAt - SelectionTimeout)).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (result.IsSuccessful || IsUnchanged(result))
            {
                RemovePending(key, pending);
                // Start the visible-step timeout only after Telegram accepted this step.
                var expiresAt = UtcNow.Add(SelectionTimeout);
                var visibleStep = owner with { ExpiresAt = expiresAt };
                var stillCurrent = _scheduled.TryUpdate(key, visibleStep, owner);
                if (stillCurrent &&
                    _interactionLocks.TryGetValue(key, out var interaction) && interaction.UserId == pending.UserId &&
                    interaction.ExpiresAt < expiresAt)
                    _interactionLocks.TryUpdate(key, interaction with { ExpiresAt = expiresAt }, interaction);
                // Complete the client's click after Telegram accepted the new keyboard.
                // A lost acknowledgement must never replay a delivered step or reservation.
                await AcknowledgePromptAsync(key, pending, null, ct);
            }
            else
            {
                if (!RetryPrompt(key, pending))
                    await AcknowledgePromptAsync(key, pending, PromptFailureMessage(key, pending), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Deferred reservation prompt failed");
            if (!RetryPrompt(key, pending))
                await AcknowledgePromptAsync(key, pending, PromptFailureMessage(key, pending), ct);
        }
        finally
        {
            gate.Release();
            _promptSlots.Release();
            WakeWorker();
        }
    }

    private bool RetryPrompt((string ChatId, long MessageId) key, PendingPrompt pending)
    {
        if (_scheduled.TryGetValue(key, out var owner) && ReferenceEquals(owner, pending.Owner) &&
            owner.ExpiresAt > UtcNow && pending.Attempts + 1 < MaxPromptAttempts)
        {
            // Retry the same presentation, never replay the customer's reservation action.
            return _pendingPrompts.TryUpdate(key, pending with
            {
                Attempts = pending.Attempts + 1,
                NextAttemptAt = UtcNow.AddMilliseconds(250 * (pending.Attempts + 1))
            }, pending);
        }
        else
            RemovePending(key, pending);
        return false;
    }

    private async Task AcknowledgePromptAsync((string ChatId, long MessageId) key,
        PendingPrompt pending, string? errorMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pending.CallbackQueryId)) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            // Acknowledgement confirms receipt, not that the client's channel view refreshed.
            var result = await _sender.AnswerCallbackAsync(pending.CallbackQueryId,
                errorMessage ?? pending.SuccessMessage ?? "انتخاب شما دریافت شد.", budget.Token, showAlert: errorMessage is not null);
            _logger.LogInformation("Deferred reservation acknowledgement callback={CallbackId} chat={ChatId} post={PostId} successful={Successful} stepAccepted={StepAccepted} error={Error}",
                pending.CallbackQueryId, key.ChatId, key.MessageId, result.IsSuccessful, errorMessage is null,
                result.Error);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning("Reservation acknowledgement failed callback={CallbackId} type={ErrorType}",
                pending.CallbackQueryId, error.GetType().Name);
        }
    }

    private string? PromptFailureMessage((string ChatId, long MessageId) key, PendingPrompt pending) =>
        _scheduled.TryGetValue(key, out var owner) && ReferenceEquals(owner, pending.Owner)
            ? "نمایش مرحلهٔ بعد انجام نشد؛ لطفاً دوباره تلاش کنید."
            : null;

    private void RemovePending((string ChatId, long MessageId) key, PendingPrompt pending) =>
        ((ICollection<KeyValuePair<(string, long), PendingPrompt>>)_pendingPrompts).Remove(new(key, pending));

    private static bool IsUnchanged(TelegramSendResult result) =>
        result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;

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

    internal Task RestoreDueAsync(DateTime now, CancellationToken ct) =>
        Task.WhenAll(StartDueRestores(now, ct));

    private IEnumerable<Task> StartDueRestores(DateTime now, CancellationToken ct)
    {
        foreach (var item in _scheduled.Where(item => item.Value.ExpiresAt <= now).ToArray())
        {
            if (!_maintenanceSlots.Wait(0)) continue;
            var gate = _postGates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                _maintenanceSlots.Release();
                continue;
            }
            yield return RestorePostAsync(item.Key, item.Value, gate, ct);
        }
    }

    private async Task RestorePostAsync((string ChatId, long MessageId) key, ScheduledRestore expected,
        SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            if (!_scheduled.TryGetValue(key, out var current) || !ReferenceEquals(current, expected)) return;
            var result = !string.IsNullOrWhiteSpace(current.PhotoFileId)
                ? await _sender.EditPhotoAsync(key.ChatId, key.MessageId, current.PhotoFileId, current.Caption, current.Rows, ct)
                : current.KeyboardOnly
                ? await _sender.EditReplyMarkupAsync(key.ChatId, key.MessageId, current.Rows, ct)
                : await _sender.EditPhotoCaptionAsync(key.ChatId, key.MessageId, current.Caption, current.Rows, ct);
            if (result.IsSuccessful || IsUnchanged(result))
                ((ICollection<KeyValuePair<(string, long), ScheduledRestore>>)_scheduled).Remove(new(key, current));
            else
            {
                _logger.LogWarning("Telegram reservation prompt restore failed: {Error}", result.Error);
                _scheduled.TryUpdate(key, current with { ExpiresAt = UtcNow.AddSeconds(5) }, current);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Telegram reservation prompt restore failed");
            _scheduled.TryUpdate(key, expected with { ExpiresAt = UtcNow.AddSeconds(5) }, expected);
        }
        finally
        {
            gate.Release();
            _maintenanceSlots.Release();
            WakeWorker();
        }
    }

    public bool TryAcquireInteraction(
        string chatId, long messageId, long userId, TimeSpan timeout)
    {
        var key = (chatId, messageId);
        while (true)
        {
            var now = UtcNow;
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
        _scheduled[(chatId, messageId)] = new(UtcNow.Add(delay), caption, rows, keyboardOnly, photoFileId);

    public void Cancel(string chatId, long messageId)
    {
        _scheduled.TryRemove((chatId, messageId), out _);
        _pendingPrompts.TryRemove((chatId, messageId), out _);
    }

    public void ScheduleDelete(string chatId, long messageId, TimeSpan delay) =>
        _scheduledDeletes[(chatId, messageId)] = UtcNow.Add(delay);

    private void WakeWorker()
    {
        // Coalesce wakeups; each post still has one serialized writer.
        try { _workAvailable.Release(); }
        catch (SemaphoreFullException) { }
    }

    private IEnumerable<Task> StartDueDeletes(DateTime now, CancellationToken ct)
    {
        foreach (var item in _scheduledDeletes.Where(item => item.Value <= now).ToArray())
        {
            if (!_maintenanceSlots.Wait(0)) continue;
            if (!((ICollection<KeyValuePair<(string, long), DateTime>>)_scheduledDeletes).Remove(item))
            {
                _maintenanceSlots.Release();
                continue;
            }
            yield return DeletePostAsync(item.Key, ct);
        }
    }

    private async Task DeletePostAsync((string ChatId, long MessageId) key, CancellationToken ct)
    {
        try
        {
            var result = await _sender.DeleteMessageAsync(key.ChatId, key.MessageId, ct);
            if (!result.IsSuccessful)
                _logger.LogWarning("Telegram temporary message delete failed: {Error}", result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { _logger.LogWarning(error, "Telegram temporary message delete failed"); }
        finally { _maintenanceSlots.Release(); WakeWorker(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inFlight = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                inFlight.RemoveAll(task => task.IsCompleted);
                var now = UtcNow;
                foreach (var item in _interactionLocks.Where(item => item.Value.ExpiresAt <= now).ToArray())
                    ((ICollection<KeyValuePair<(string, long), InteractionLock>>)_interactionLocks).Remove(item);
                // Slow edits, restores and deletes must not stop new prompts on other posts.
                inFlight.AddRange(StartPendingPrompts(stoppingToken));
                inFlight.AddRange(StartDueRestores(now, stoppingToken));
                inFlight.AddRange(StartDueDeletes(now, stoppingToken));
                await _workAvailable.WaitAsync(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(inFlight); }
    }
}
