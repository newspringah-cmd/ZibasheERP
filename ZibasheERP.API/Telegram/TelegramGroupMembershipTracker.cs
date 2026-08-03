using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public interface ITelegramGroupMembershipTracker
{
    Task TrackAsync(TelegramChatMemberUpdated update, CancellationToken cancellationToken);
    Task MarkUnavailableAsync(string chatId, CancellationToken cancellationToken);
    Task<TelegramGroupLinkResult> LinkByInvoiceAsync(
        TelegramChat chat,
        string invoiceNumber,
        CancellationToken cancellationToken);
}

public enum TelegramGroupLinkStatus
{
    Linked,
    AlreadyLinked,
    InvoiceNotFound,
    GroupLinkedToAnotherCustomer,
    CustomerLinkedToAnotherGroup
}

public sealed record TelegramGroupLinkResult(
    TelegramGroupLinkStatus Status,
    string? CustomerName = null);

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

    public async Task<TelegramGroupLinkResult> LinkByInvoiceAsync(
        TelegramChat chat,
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        var normalizedInvoiceNumber = invoiceNumber.Trim();
        var invoice = await context.Invoices
            .AsNoTracking()
            .Include(value => value.Order)
            .ThenInclude(value => value!.Customer)
            .FirstOrDefaultAsync(
                value => value.InvoiceNumber == normalizedInvoiceNumber && !value.IsDeleted,
                cancellationToken);
        var customer = invoice?.Order?.Customer;
        if (customer is null || invoice!.Order!.IsDeleted || customer.IsDeleted)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.InvoiceNotFound);

        var chatId = chat.Id.ToString();
        var existingByChat = await context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.ChatId == chatId && !value.IsDeleted,
            cancellationToken);
        if (existingByChat is not null && existingByChat.CustomerId != customer.Id)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.GroupLinkedToAnotherCustomer);

        var existingByCustomer = await context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.CustomerId == customer.Id && !value.IsDeleted,
            cancellationToken);
        if (existingByCustomer is not null && existingByCustomer.ChatId != chatId)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.CustomerLinkedToAnotherGroup);

        var now = DateTime.UtcNow;
        var group = existingByChat ?? existingByCustomer;
        var alreadyLinked = group is not null && group.IsActive && group.ChatId == chatId;
        if (group is null)
        {
            group = new ZibasheERP.Domain.Entities.CustomerTelegramGroup
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                ChatId = chatId,
                CreatedAt = now,
                LinkedAt = now
            };
            context.CustomerTelegramGroups.Add(group);
        }

        group.Title = string.IsNullOrWhiteSpace(chat.Title) ? chatId : chat.Title.Trim();
        group.Username = NormalizeUsername(chat.Username);
        group.IsActive = true;
        group.IsDeleted = false;
        group.LastSeenAt = now;
        group.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);

        return new TelegramGroupLinkResult(
            alreadyLinked ? TelegramGroupLinkStatus.AlreadyLinked : TelegramGroupLinkStatus.Linked,
            customer.FullName);
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
