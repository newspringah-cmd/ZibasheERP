using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

internal static class TelegramReservationPostEditor
{
    private static readonly ConcurrentDictionary<(string ChatId, long MessageId), SemaphoreSlim> Gates = new();


    public static async Task<TelegramSendResult> EditReservationPostAsync(
        this ITelegramMessageSender sender, string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken, bool hasPhoto = true)
    {
        var key = (chatId, messageId);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

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
            return result;
        }
        finally { gate.Release(); }
    }

    private static bool IsUnchanged(TelegramSendResult result) =>
        result.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;
}
