namespace ZibasheERP.Application.Notifications;

public static class TelegramDeliveryFailurePolicy
{
    private static readonly string[] PermanentGroupFailures =
    [
        "bot was kicked",
        "bot is not a member",
        "chat not found",
        "not enough rights to send",
        "have no rights to send",
        "chat_write_forbidden"
    ];

    public static bool IsPermanentGroupAccessFailure(
        string recipient,
        string? error)
    {
        if (!recipient.TrimStart().StartsWith("-", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return PermanentGroupFailures.Any(value =>
            error.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
