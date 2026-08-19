using MediatR;

namespace ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

public sealed record CreateSalesListCommand(
    Guid PerfumeId,
    decimal PricePerMl,
    int TotalVolume,
    string? TelegramChannelId,
    string? Notes,
    int MinimumRequestVolumeMl = 1,
    string? EnglishName = null,
    string? ProductPageUrl = null,
    string? DisplayBrand = null,
    int Gender = 3,
    int ReleaseYear = 0,
    string? PersianName = null,
    string? TopNotes = null,
    string? MiddleNotes = null,
    string? BaseNotes = null,
    string? Accords = null) : IRequest<AdminSalesListResponse>;

public sealed record CloseSalesListCommand(Guid SalesListId)
    : IRequest<AdminSalesListResponse>;

public sealed record GetAdminSalesListsQuery(int Limit = 100)
    : IRequest<IReadOnlyCollection<AdminSalesListResponse>>;

public sealed record AdminSalesListResponse(
    Guid Id,
    Guid? BatchId,
    string? BatchNumber,
    string PerfumeName,
    string Brand,
    decimal PricePerMl,
    int TotalVolume,
    int MinimumRequestVolumeMl,
    int ReservedVolume,
    int RemainingVolume,
    bool HasBottleOwner,
    string? BottleOwnerName,
    string Status,
    DateTime OpenDate,
    DateTime? ClosedDate,
    string? TelegramChannelId,
    string? Notes,
    int PublicCode = 0);
