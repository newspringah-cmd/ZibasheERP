namespace ZibasheERP.API.Contracts.TelegramGroups;

public sealed record UpsertCustomerTelegramGroupRequest(
    string ChatId,
    string Title,
    string? Username,
    bool IsActive = true);

public sealed record CustomerTelegramGroupResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string CustomerMobile,
    string ChatId,
    string Title,
    string? Username,
    bool IsActive,
    DateTime LinkedAt,
    DateTime? LastSeenAt);
