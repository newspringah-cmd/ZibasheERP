using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Inventory.GetInventory;

public sealed class GetInventoryQueryHandler
    : IRequestHandler<GetInventoryQuery, InventoryResponse>
{
    private readonly IBatchRepository _batchRepository;
    private readonly IBottleRepository _bottleRepository;

    public GetInventoryQueryHandler(
        IBatchRepository batchRepository,
        IBottleRepository bottleRepository)
    {
        _batchRepository = batchRepository;
        _bottleRepository = bottleRepository;
    }

    public async Task<InventoryResponse> Handle(
        GetInventoryQuery request,
        CancellationToken cancellationToken)
    {
        var batches = await _batchRepository.GetForInventoryAsync(
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        var bottles = await _bottleRepository.GetActiveAsync(cancellationToken);

        var batchResponses = batches.Select(batch =>
        {
            var remaining = Math.Clamp(batch.RemainingVolumeMl, 0, batch.TotalVolumeMl);
            var used = Math.Max(0, batch.TotalVolumeMl - remaining);
            var percentage = batch.TotalVolumeMl <= 0
                ? 0
                : Math.Round(remaining / batch.TotalVolumeMl * 100, 2);

            return new InventoryBatchResponse(
                batch.Id,
                batch.BatchNumber,
                batch.PerfumeId,
                batch.Perfume.Name,
                batch.Perfume.EnglishName,
                batch.Perfume.Brand,
                batch.PurchasePrice,
                batch.Perfume.PricePerMl,
                batch.TotalVolumeMl,
                remaining,
                used,
                percentage,
                batch.Status,
                batch.PurchaseDate);
        }).ToArray();

        var bottleResponses = bottles
            .OrderBy(bottle => bottle.VolumeMl)
            .ThenBy(bottle => bottle.Name)
            .Select(bottle => new InventoryBottleResponse(
                bottle.Id,
                bottle.Name,
                bottle.VolumeMl,
                bottle.Type.ToString(),
                bottle.SalePrice,
                bottle.IsDefault))
            .ToArray();

        return new InventoryResponse(
            new InventorySummaryResponse(
                batchResponses.Length,
                batchResponses.Select(batch => batch.PerfumeId).Distinct().Count(),
                batchResponses.Sum(batch => batch.TotalVolumeMl),
                batchResponses.Sum(batch => batch.RemainingVolumeMl),
                batchResponses.Sum(batch => batch.UsedVolumeMl),
                bottleResponses.Length),
            batchResponses,
            bottleResponses);
    }
}
