using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public interface ITelegramGroupMembershipTracker
{
    Task TrackAsync(TelegramChatMemberUpdated update, CancellationToken cancellationToken);
    Task MarkUnavailableAsync(string chatId, CancellationToken cancellationToken);
}

public sealed class TelegramGroupMembershipTracker(
    AppDbContext context,
    ILogger<TelegramGroupMembershipTracker> logger) : ITelegramGroupMembershipTracker
{
    public async Task TrackAsync(
        TelegramChatMemberUpdated update,
        CancellationToken cancellationToken)
    {
        if (!IsGroup(update.Chat.Type))
            return;

        var chatId = update.Chat.Id.ToString();
        var group = await context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.ChatId == chatId && !value.IsDeleted,
            cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "Telegram bot membership changed for unmapped group {TelegramGroupChatId}.",
                chatId);
            return;
        }

        var now = DateTime.UtcNow;
        group.IsActive = TelegramGroupMembershipPolicy.CanDeliver(
            update.NewChatMember.Status,
            update.NewChatMember.IsMember,
            update.NewChatMember.CanSendMessages);
        if (!string.IsNullOrWhiteSpace(update.Chat.Title))
            group.Title = update.Chat.Title.Trim();
        group.Username = NormalizeUsername(update.Chat.Username);
        group.LastSeenAt = now;
        group.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkUnavailableAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        var group = await context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.ChatId == chatId && !value.IsDeleted,
            cancellationToken);
        if (group is null || !group.IsActive)
            return;

        group.IsActive = false;
        group.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Telegram group {TelegramGroupChatId} was disabled after a permanent delivery failure.",
            chatId);
    }

    private static bool IsGroup(string chatType) =>
        string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(chatType, "supergroup", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeUsername(string? username)
    {
        var normalized = username?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
