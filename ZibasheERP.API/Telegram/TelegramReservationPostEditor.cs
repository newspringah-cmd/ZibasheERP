using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

internal static class TelegramReservationPostEditor
{
    private sealed record Refresh(DateTime DueAt,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows);
    private static readonly ConcurrentDictionary<(string ChatId, long MessageId), SemaphoreSlim> Gates = new();
    private static readonly ConcurrentDictionary<(string ChatId, long MessageId), Refresh> Pending = new();

    public static void CancelRefresh(string chatId, long messageId) => Pending.TryRemove((chatId, messageId), out _);

    public static async Task<TelegramSendResult> EditReservationPostAsync(
        this ITelegramMessageSender sender, string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken, bool hasPhoto = true)
    {
        var key = (chatId, messageId);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        CancelRefresh(chatId, messageId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Clear the old keyboard while preparing the caption, then publish the
            // next step last through its dedicated method as a real markup change.
            IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> empty = [];
            var content = hasPhoto
                ? await sender.EditPhotoCaptionAsync(chatId, messageId, caption, empty, cancellationToken)
                : await sender.EditTextWithKeyboardAsync(chatId, messageId, caption, empty, cancellationToken);
            if (!content.IsSuccessful && !IsUnchanged(content)) return content;
            var result = await sender.EditReplyMarkupAsync(chatId, messageId, rows, cancellationToken);
            if (result.IsSuccessful || IsUnchanged(result))
                Pending[key] = new(DateTime.UtcNow.AddSeconds(2), rows);
            return result;
        }
        finally { gate.Release(); }
    }

    internal static async Task RefreshDueAsync(ITelegramMessageSender sender, ILogger logger,
        DateTime now, CancellationToken cancellationToken)
    {
        foreach (var item in Pending.Where(value => value.Value.DueAt <= now).ToArray())
        {
            var gate = Gates.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                // A newer stage or cancellation supersedes this refresh.
                if (!Pending.TryGetValue(item.Key, out var current) || !ReferenceEquals(current, item.Value)) continue;
                Pending.TryRemove(item.Key, out _);
                var cleared = await sender.EditReplyMarkupAsync(item.Key.ChatId, item.Key.MessageId,
                    [], cancellationToken);
                if (!cleared.IsSuccessful && !IsUnchanged(cleared))
                {
                    logger.LogWarning("Reservation background keyboard clear failed: {MessageId}, {Error}", item.Key.MessageId, cleared.Error);
                    continue;
                }
                var result = await sender.EditReplyMarkupAsync(item.Key.ChatId, item.Key.MessageId,
                    current.Rows, cancellationToken);
                logger.LogInformation("Reservation background keyboard refresh: {MessageId}, success {Success}, error {Error}",
                    item.Key.MessageId, result.IsSuccessful, result.Error);
                if (!result.IsSuccessful && !IsUnchanged(result))
                {
                    // Restore the same stage if a transient failure left the keyboard empty.
                    result = await sender.EditReplyMarkupAsync(item.Key.ChatId, item.Key.MessageId,
                        current.Rows, cancellationToken);
                    if (!result.IsSuccessful && !IsUnchanged(result))
                        logger.LogWarning("Reservation background keyboard recovery failed: {MessageId}, {Error}", item.Key.MessageId, result.Error);
                }
            }
            finally { gate.Release(); }
        }
    }

    private static bool IsUnchanged(TelegramSendResult result) =>
        result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;
}
