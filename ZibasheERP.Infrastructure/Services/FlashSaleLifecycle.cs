using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class FlashSaleLifecycle
{
    public static async Task<FlashSalePromotion?> EndAsync(AppDbContext db, Guid id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var initial = await db.FlashSalePromotions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id && !value.IsDeleted, ct);
        if (initial is null) return null;
        var origin = await db.SalesLists.AsNoTracking().SingleAsync(value => value.Id == initial.SalesListId, ct);
        if(origin.IsInventoryOffer)
            await db.SalesLists.FromSqlInterpolated($"SELECT * FROM [SalesLists] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={origin.Id}")
                .AsNoTracking().ToArrayAsync(ct);
        else
            await db.SalesLists.FromSqlInterpolated($"SELECT * FROM [SalesLists] WITH (UPDLOCK,HOLDLOCK) WHERE [PerfumeId]={origin.PerfumeId} AND COALESCE([StablePublicCode],[PublicCode])={origin.DisplayCode}")
                .AsNoTracking().ToArrayAsync(ct);
        var promotion = await db.FlashSalePromotions.SingleAsync(value => value.Id == id, ct);
        await db.Entry(promotion).ReloadAsync(ct);
        var now = DateTime.UtcNow;
        if (promotion.EndsAtUtc > now) { promotion.EndsAtUtc = now; promotion.UpdatedAt = now; await db.SaveChangesAsync(ct); }
        await transaction.CommitAsync(ct);
        return promotion;
    }
}
