using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

/// <summary>
/// Keeps uninvoiced sales-list request bottle snapshots aligned with the current
/// bottle catalog. Ambiguous legacy requests are deliberately left untouched.
/// </summary>
public sealed class TelegramBottlePriceSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramBottlePriceSyncWorker> _logger;

    public TelegramBottlePriceSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TelegramBottlePriceSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var bottles = await db.Bottles.AsNoTracking()
                .Where(bottle => !bottle.IsDeleted && bottle.IsActive)
                .ToArrayAsync(stoppingToken);
            var bottlesById = bottles.ToDictionary(bottle => bottle.Id);
            var defaultNormalBottles = bottles
                .Where(bottle => bottle.IsDefault && bottle.Type == BottleType.Normal)
                .GroupBy(bottle => bottle.VolumeMl)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single());

            var requests = await db.SalesListRequests
                .Where(request => !request.IsDeleted &&
                    request.Status != SalesListRequestStatus.Cancelled &&
                    request.Status != SalesListRequestStatus.Expired &&
                    request.Status != SalesListRequestStatus.Invoiced &&
                    !request.SalesList.IsDeleted &&
                    request.SalesList.Status != SalesListStatus.Cancelled &&
                    request.SalesList.Status != SalesListStatus.Invoiced &&
                    !db.OrderItems.Any(item => !item.IsDeleted &&
                        item.SourceSalesListRequestId == request.Id))
                .ToArrayAsync(stoppingToken);

            var linked = 0;
            var priceUpdated = 0;
            var freeZeroed = 0;
            var unresolved = 0;
            var now = DateTime.UtcNow;

            foreach (var request in requests)
            {
                if (request.IsBottleOwner || request.IsComplimentaryBottle)
                {
                    if (request.BottlePrice == 0) continue;
                    request.BottlePrice = 0;
                    request.UpdatedAt = now;
                    freeZeroed++;
                    continue;
                }

                Bottle? bottle = null;
                if (request.BottleId.HasValue)
                {
                    bottlesById.TryGetValue(request.BottleId.Value, out bottle);
                }
                else if (defaultNormalBottles.TryGetValue(request.VolumeMl, out bottle))
                {
                    request.BottleId = bottle.Id;
                    request.UpdatedAt = now;
                    linked++;
                }

                if (bottle is null || bottle.SalePrice <= 0)
                {
                    unresolved++;
                    _logger.LogWarning(
                        "Bottle price sync skipped ambiguous request {RequestId} ({Identity}), volume {VolumeMl} ml, bottle {BottleId}.",
                        request.Id,
                        string.IsNullOrWhiteSpace(request.TelegramUsername)
                            ? request.TelegramUserId
                            : $"@{request.TelegramUsername.Trim().TrimStart('@')}",
                        request.VolumeMl,
                        request.BottleId);
                    continue;
                }

                if (request.BottlePrice == bottle.SalePrice) continue;
                request.BottlePrice = bottle.SalePrice;
                request.UpdatedAt = now;
                priceUpdated++;
            }

            if (linked > 0 || priceUpdated > 0 || freeZeroed > 0)
                await db.SaveChangesAsync(stoppingToken);

            _logger.LogInformation(
                "Bottle price sync completed. Scanned={Scanned}, Linked={Linked}, PriceUpdated={PriceUpdated}, FreeZeroed={FreeZeroed}, Unresolved={Unresolved}.",
                requests.Length, linked, priceUpdated, freeZeroed, unresolved);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bottle price synchronization failed.");
        }
    }
}
