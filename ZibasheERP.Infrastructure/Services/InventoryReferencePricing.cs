using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
namespace ZibasheERP.Infrastructure.Services;

/// <summary>Apply each observed reference increase once, retaining a package's custom price offset.</summary>
public static class InventoryReferencePricing
{
    public sealed record CurrentPrice(decimal PricePerMl,decimal? ReferencePricePerMl);

    public static Task<decimal?> ReferencePriceAsync(AppDbContext db,Guid referenceId,CancellationToken ct=default) =>
        (from original in db.SalesLists.AsNoTracking()
         where original.Id==referenceId && !original.IsInventoryOffer
         from current in db.SalesLists.AsNoTracking().Where(value=>!value.IsDeleted&&!value.IsInventoryOffer&&
             value.PerfumeId==original.PerfumeId&&(value.StablePublicCode??value.PublicCode)==(original.StablePublicCode??original.PublicCode)&&
             (value.Id==original.Id||value.TelegramMessageId!=null))
             .OrderByDescending(value=>value.OpenDate).ThenByDescending(value=>value.CreatedAt).ThenByDescending(value=>value.Id).Take(1)
         select (decimal?)current.PricePerMl).FirstOrDefaultAsync(ct);

    private static IQueryable<Guid> DirtyIds(AppDbContext db) =>
        from stock in db.SalesLists.AsNoTracking()
        join original in db.SalesLists.AsNoTracking() on stock.InventoryReferenceListId equals (Guid?)original.Id
        where !stock.IsDeleted&&stock.IsInventoryOffer&&stock.InventoryCode!=null&&
            (stock.Status==SalesListStatus.Open||stock.Status==SalesListStatus.Full)&&!original.IsInventoryOffer
        let reference=(from current in db.SalesLists.AsNoTracking()
            where !current.IsDeleted&&!current.IsInventoryOffer&&current.PerfumeId==original.PerfumeId&&
                (current.StablePublicCode??current.PublicCode)==(original.StablePublicCode??original.PublicCode)&&
                (current.Id==original.Id||current.TelegramMessageId!=null)
            orderby current.OpenDate descending,current.CreatedAt descending,current.Id descending
            select (decimal?)current.PricePerMl).FirstOrDefault()
        where reference>0 && (stock.InventoryReferencePricePerMl==null||stock.InventoryReferencePricePerMl!=reference)
        orderby stock.Id
        select stock.Id;

    public static async Task<int> SynchronizeAsync(AppDbContext db,CancellationToken ct=default)
    {
        var ids=await DirtyIds(db).Take(100).ToArrayAsync(ct);
        foreach(var id in ids)await SynchronizeOneAsync(db,id,ct);
        return ids.Length;
    }

    public static async Task<CurrentPrice?> SynchronizeOneAsync(AppDbContext db,Guid id,CancellationToken ct=default)
    {
        await using var ownedTransaction=db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // Customer confirmation and invoice approval take this same row lock.
        var stock=await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}")
            .SingleOrDefaultAsync(ct);
        if(stock is null)return null;
        await db.Entry(stock).ReloadAsync(ct);
        if(stock.IsDeleted||!stock.IsInventoryOffer||stock.InventoryCode is null||stock.InventoryReferenceListId is null||
            stock.Status is not(SalesListStatus.Open or SalesListStatus.Full))return null;
        var reference=await ReferencePriceAsync(db,stock.InventoryReferenceListId.Value,ct);
        if(reference>0 && reference!=stock.InventoryReferencePricePerMl)
        {
            // Null baselines are initialized without guessing a historical price.
            var increase=stock.InventoryReferencePricePerMl.HasValue?Math.Max(0,reference.Value-stock.InventoryReferencePricePerMl.Value):0;
            if(increase>0)
            {
                var previousPrice=stock.PricePerMl;
                stock.PricePerMl+=increase;
                db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,Channel="ReadyInventoryRefresh",
                    EventType="InventoryReferencePriceIncreased",Recipient=stock.Id.ToString(),
                    Payload=System.Text.Json.JsonSerializer.Serialize(new{ReferenceListId=stock.InventoryReferenceListId,PreviousReferencePrice=stock.InventoryReferencePricePerMl,
                        ReferencePrice=reference,PreviousInventoryPrice=previousPrice,InventoryPrice=stock.PricePerMl,IncreasePerMl=increase})});
            }
            stock.InventoryReferencePricePerMl=reference;
            stock.UpdatedAt=DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        if(ownedTransaction is not null)await ownedTransaction.CommitAsync(ct);
        return new(stock.PricePerMl,stock.InventoryReferencePricePerMl);
    }
}
