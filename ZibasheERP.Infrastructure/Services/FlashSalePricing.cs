using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class FlashSalePricing
{
    public sealed record Quote(decimal PricePerMl, Guid? PromotionId);

    public static async Task<Quote> QuoteAsync(AppDbContext db, SalesList list, DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if(list.IsInventoryOffer&&list.InventoryCode.HasValue&&list.InventoryReferenceListId.HasValue)
        {
            var current=await InventoryReferencePricing.SynchronizeOneAsync(db,list.Id,cancellationToken);
            if(current is not null){list.PricePerMl=current.PricePerMl;list.InventoryReferencePricePerMl=current.ReferencePricePerMl;}
        }
        // The stable channel list can roll into a new bottle cycle while an offer
        // is running. Keep the original campaign, price and exclusive deadline.
        var promotion = await (from value in db.FlashSalePromotions.AsNoTracking()
            join origin in db.SalesLists.AsNoTracking() on value.SalesListId equals origin.Id
            where !value.IsDeleted && value.CancelledAtUtc == null &&
                value.StartsAtUtc <= utcNow && value.EndsAtUtc > utcNow &&
                (value.SalesListId == list.Id || (!list.IsInventoryOffer && !origin.IsInventoryOffer &&
                 origin.PerfumeId == list.PerfumeId &&
                 (origin.StablePublicCode ?? origin.PublicCode) == list.DisplayCode &&
                 list.OpenDate >= value.StartsAtUtc && list.OpenDate < value.EndsAtUtc))
            select value).SingleOrDefaultAsync(cancellationToken);
        return promotion is null ? new(list.PricePerMl, null) : new(promotion.DiscountedPricePerMl, promotion.Id);
    }

    public static decimal InvoicePrice(SalesListRequest request, decimal ordinaryListPrice) =>
        (request.FlashSalePromotionId.HasValue || request.SalesList?.IsInventoryOffer == true) ? request.PerfumePricePerMl : ordinaryListPrice;
}
