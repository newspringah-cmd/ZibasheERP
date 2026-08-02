namespace ZibasheERP.API.Contracts.TelegramGroups;

public sealed record UpsertCustomerTelegramGroupRequest(
    string ChatId,
    string Title,
    string? Username,
    bool IsActive = false);

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

public sealed record TelegramGroupCsvImportResponse(
    bool DryRun,
    int TotalRows,
    int SelectedRows,
    int Created,
    int Updated,
    int Unchanged,
    int IssueCount,
    IReadOnlyCollection<TelegramGroupCsvImportIssueResponse> Issues);

public sealed record TelegramGroupCsvImportIssueResponse(
    int? RowNumber,
    string Code,
    string Message,
    string? CustomerUsername,
    string? ChatId);

public sealed record TelegramGroupReadinessResponse(
    int TotalCustomers,
    int CustomersWithUsername,
    int MappedCustomers,
    int UnmappedCustomers,
    int ActiveGroups,
    int InactiveGroups,
    int GroupsNeverSeenByBot,
    decimal MappingPercent,
    decimal DeliveryReadyPercent,
    bool IsReadyForAutomatedDelivery);

public sealed record TelegramGroupDeliveryTestResponse(
    Guid NotificationId,
    Guid CustomerId,
    string ChatId,
    string Status);
