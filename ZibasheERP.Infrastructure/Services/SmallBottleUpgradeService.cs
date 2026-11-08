using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class SmallBottleUpgradeService(AppDbContext db)
{
    public static bool CanUpgrade(SalesListRequest r) => !r.IsDeleted &&
        r.Status==SalesListRequestStatus.Confirmed && r.Kind==SalesListRequestKind.CurrentBottle &&
        r.VolumeMl>0 && r.VolumeMl==decimal.Truncate(r.VolumeMl) && !r.IsBottleOwner &&
        r.SalesList is {IsDeleted:false,IsInventoryOffer:false} &&
        r.SalesList.Status is not(SalesListStatus.Invoiced or SalesListStatus.Cancelled or SalesListStatus.QueuedForInvoice);
    public async Task<SalesListRequest> PreviewAsync(Guid requestId,CancellationToken ct=default)
    {
        var r=await db.SalesListRequests.AsNoTracking().Include(v=>v.SalesList).Include(v=>v.Bottle)
            .SingleOrDefaultAsync(v=>v.Id==requestId,ct);
        if(r is null||!CanUpgrade(r))throw new InvalidOperationException("فقط شیشهٔ آیتم فعال لیست، قبل از صدور فاکتور، قابل تغییر است؛ موجودی آماده و صاحب باتل شامل این گزینه نیستند.");
        if(await db.InvoiceIssuanceBatchSalesLists.AnyAsync(v=>v.SalesListId==r.SalesListId,ct)||
            await db.OrderItems.AnyAsync(v=>!v.IsDeleted&&v.SourceSalesListRequestId==r.Id,ct))
            throw new InvalidOperationException("برای این آیتم فاکتور صادر شده و شیشه قابل تغییر نیست.");
        return r;
    }
    public async Task UpgradeAsync(Guid requestId,byte[] expectedVersion,Guid targetId,decimal expectedPrice,long actor,CancellationToken ct=default)
    {
        if(actor<=0)throw new InvalidOperationException("ادمین معتبر نیست.");
        var listId=await db.SalesListRequests.AsNoTracking().Where(v=>v.Id==requestId).Select(v=>(Guid?)v.SalesListId).SingleOrDefaultAsync(ct)
            ??throw new InvalidOperationException("آیتم پیدا نشد.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        // Same lock order as invoice issuance: list first, then its requests.
        await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={listId}").AsNoTracking().SingleAsync(ct);
        var r=await db.SalesListRequests.FromSqlInterpolated($"SELECT * FROM SalesListRequests WITH(UPDLOCK,HOLDLOCK) WHERE Id={requestId}").SingleAsync(ct);
        await db.Entry(r).ReloadAsync(ct);
        await PreviewAsync(requestId,ct);
        if(!r.RowVersion.SequenceEqual(expectedVersion))throw new InvalidOperationException("آیتم پس از پیش‌نمایش تغییر کرده؛ دوباره انتخاب کنید.");
        var target=await db.Bottles.FromSqlInterpolated($"SELECT * FROM Bottles WITH(HOLDLOCK) WHERE Id={targetId}").AsNoTracking().SingleOrDefaultAsync(ct);
        if(target is not {IsDeleted:false,IsActive:true}||target.SalePrice<=0||target.VolumeMl<r.VolumeMl||target.Type is not(BottleType.Normal or BottleType.Fancy))
            throw new InvalidOperationException("شیشهٔ فعال با قیمت معتبر و ظرفیت کافی برای حجم عطر انتخاب کنید.");
        if(target.Id==r.BottleId)throw new InvalidOperationException("این شیشه از قبل برای آیتم انتخاب شده است.");
        if(target.SalePrice!=expectedPrice)throw new InvalidOperationException("قیمت شیشه تغییر کرده؛ پیش‌نمایش تازه بگیرید.");
        var previous=r.BottleId;
        r.BottleId=target.Id;r.BottlePrice=r.IsComplimentaryBottle?0:target.SalePrice;r.UpdatedAt=DateTime.UtcNow;
        db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,Channel="ReadyInventoryRefresh",EventType="AdminBottleChanged",
            Recipient=listId.ToString(),Payload=JsonSerializer.Serialize(new{RequestId=r.Id,Actor=actor,PreviousBottleId=previous,BottleId=target.Id,r.BottlePrice,r.VolumeMl})});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
