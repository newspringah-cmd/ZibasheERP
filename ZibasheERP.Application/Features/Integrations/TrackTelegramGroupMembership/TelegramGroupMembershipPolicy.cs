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
}
