namespace ZibasheERP.API.Telegram;

internal static class TelegramReservationPostEditor
{
    public static async Task<TelegramSendResult> EditReservationPostAsync(
        this ITelegramMessageSender sender, string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken, bool hasPhoto = true)
    {
        // Clear the old keyboard while preparing the caption, then publish the
        // next step last through its dedicated method as a real markup change.
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> empty = [];
        var content = hasPhoto
            ? await sender.EditPhotoCaptionAsync(chatId, messageId, caption, empty, cancellationToken)
            : await sender.EditTextWithKeyboardAsync(chatId, messageId, caption, empty, cancellationToken);
        if (!content.IsSuccessful &&
            content.Error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) != true)
            return content;
        return await sender.EditReplyMarkupAsync(chatId, messageId, rows, cancellationToken);
    }
}
