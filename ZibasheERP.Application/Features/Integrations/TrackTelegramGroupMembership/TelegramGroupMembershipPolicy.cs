namespace ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;

public static class TelegramGroupMembershipPolicy
{
    public static bool CanDeliver(
        string? status,
        bool? isMember,
        bool? canSendMessages) => status?.Trim().ToLowerInvariant() switch
        {
            "administrator" or "member" => true,
            "restricted" => isMember == true && canSendMessages == true,
            _ => false
        };

    public static TelegramGroupLinkAvailability ApplyAvailability(
        bool isActive,
        bool restoreOnBotRejoin,
        bool canDeliver)
    {
        if (canDeliver)
            return restoreOnBotRejoin
                ? new TelegramGroupLinkAvailability(true, false)
                : new TelegramGroupLinkAvailability(isActive, false);

        return isActive
            ? new TelegramGroupLinkAvailability(false, true)
            : new TelegramGroupLinkAvailability(false, restoreOnBotRejoin);
    }
}

public readonly record struct TelegramGroupLinkAvailability(
    bool IsActive,
    bool RestoreOnBotRejoin);
