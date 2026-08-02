using MediatR;

namespace ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

public sealed record CreateSalesListCommand(
    Guid BatchId,
    decimal PricePerMl,
    int TotalVolume,
    string? TelegramChannelId,
    string? Notes) : IRequest<AdminSalesListResponse>;

public sealed record CloseSalesListCommand(Guid SalesListId)
    : IRequest<AdminSalesListResponse>;

public sealed record GetAdminSalesListsQuery(int Limit = 100)
    : IRequest<IReadOnlyCollection<AdminSalesListResponse>>;

public sealed record AdminSalesListResponse(
    Guid Id,
    Guid BatchId,
    string BatchNumber,
    string PerfumeName,
    string Brand,
    decimal PricePerMl,
    int TotalVolume,
    int ReservedVolume,
    int RemainingVolume,
    bool HasBottleOwner,
    string? BottleOwnerName,
    string Status,
    DateTime OpenDate,
    DateTime? ClosedDate,
    string? TelegramChannelId,
    string? Notes);
