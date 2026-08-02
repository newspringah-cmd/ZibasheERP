using MediatR;

namespace ZibasheERP.Application.Features.Inventory.GetInventory;

public sealed record GetInventoryQuery(int Limit = 100)
    : IRequest<InventoryResponse>;

public sealed record InventoryResponse(
    InventorySummaryResponse Summary,
    IReadOnlyCollection<InventoryBatchResponse> Batches,
    IReadOnlyCollection<InventoryBottleResponse> ActiveBottles);

public sealed record InventorySummaryResponse(
    int BatchCount,
    int PerfumeCount,
    decimal TotalVolumeMl,
    decimal RemainingVolumeMl,
    decimal ReservedOrConsumedVolumeMl,
    int ActiveBottleCount);

public sealed record InventoryBatchResponse(
    Guid BatchId,
    string BatchNumber,
    Guid PerfumeId,
    string PerfumeName,
    string EnglishName,
    string Brand,
    decimal PurchasePrice,
    decimal PricePerMl,
    decimal TotalVolumeMl,
    decimal RemainingVolumeMl,
    decimal UsedVolumeMl,
    decimal RemainingPercentage,
    string Status,
    DateTime PurchaseDate);

public sealed record InventoryBottleResponse(
    Guid BottleId,
    string Name,
    int VolumeMl,
    string Type,
    decimal SalePrice,
    bool IsDefault);
