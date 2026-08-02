namespace ZibasheERP.Application.Notifications;

public sealed record TelegramPaymentCommand(string OrderNumber, string TransactionId);

public static class TelegramPaymentCommandParser
{
    public static TelegramPaymentCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !string.Equals(parts[0].Split('@', 2)[0], "/pay", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var orderNumber = parts[1].Trim();
        var transactionId = parts[2].Trim();
        return orderNumber.Length is > 0 and <= 50 &&
            transactionId.Length is > 0 and <= 100
                ? new(orderNumber, transactionId)
                : null;
    }
}
