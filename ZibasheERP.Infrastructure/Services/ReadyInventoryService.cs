using System.Data;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class ReadyInventoryService(AppDbContext db)
{
    // Dedicated stock packaging, never a customer-selectable decant bottle.
    public static readonly Guid OriginalBottleId = new("5a30c683-3c4b-42b3-978a-ec26ca239d02");
    public const string OriginalBottleName = "باتل اصلی رایگان";
    public sealed record Create(Guid Id, Guid? ReferenceId, string Name, string Photo, decimal PricePerMl,
        decimal Volume, Guid BottleId, decimal BottlePrice, string Position, long Actor,string? Description=null);
    public async Task<SalesList> CreateAsync(Create input, CancellationToken ct=default)
    {
        if(input.Id==Guid.Empty || input.Actor<=0 || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length>200 ||
            string.IsNullOrWhiteSpace(input.Photo) || input.Photo.Length>500 || input.PricePerMl<=0 || input.PricePerMl>1000000000000m || input.Volume is <0.5m or >1000 || input.Volume*2!=decimal.Truncate(input.Volume*2) ||
            input.BottlePrice<0 || input.BottlePrice>1000000000000m || string.IsNullOrWhiteSpace(input.Position) || input.Position.Trim().Length>150 ||
            (input.Description?.Trim().Length??0)>400 ||
            decimal.Round(input.PricePerMl,2)!=input.PricePerMl || decimal.Round(input.BottlePrice,2)!=input.BottlePrice)
            throw new InvalidOperationException("اطلاعات موجودی معتبر نیست.");
        await using var tx=db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct) : null;
        // Serialize code allocation across processes, including sold/deleted codes.
        await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sp_getapplock @Resource='ready-inventory-code', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @r<0 THROW 51000,'Inventory code allocation busy',1;",ct);
        var existing=await db.SalesLists.SingleOrDefaultAsync(v=>v.Id==input.Id,ct);
        if(existing is not null)
        {
            if(!existing.IsInventoryOffer || existing.InventoryCreatedByUserId!=input.Actor || existing.InventoryReferenceListId!=input.ReferenceId || existing.TotalVolume!=input.Volume || existing.PricePerMl!=input.PricePerMl || existing.FixedBottleId!=input.BottleId || existing.FixedBottlePrice!=input.BottlePrice || existing.TelegramPhotoFileId!=input.Photo || existing.StoragePosition!=input.Position.Trim() || existing.InventoryPublicDescription!=(string.IsNullOrWhiteSpace(input.Description)?null:input.Description.Trim()))throw new InvalidOperationException("درخواست تکراری نامعتبر است.");
            if(tx is not null)await tx.CommitAsync(ct);return existing;
        }
        Bottle bottle;
        if (input.BottleId == OriginalBottleId)
        {
            if (input.BottlePrice != 0) throw new InvalidOperationException("هزینهٔ باتل اصلی باید صفر باشد.");
            bottle = await db.Bottles.SingleOrDefaultAsync(v => v.Id == OriginalBottleId, ct)
                ?? new Bottle { Id=OriginalBottleId, Name=OriginalBottleName, VolumeMl=1000,
                    Type=BottleType.Normal, SalePrice=0, IsDefault=false, IsActive=true, CreatedAt=DateTime.UtcNow };
            if (db.Entry(bottle).State == EntityState.Detached) db.Bottles.Add(bottle);
            if (bottle.IsDeleted || !bottle.IsActive || bottle.Name != OriginalBottleName || bottle.SalePrice != 0 || bottle.VolumeMl < input.Volume)
                throw new InvalidOperationException("تنظیمات باتل اصلی موجودی نیازمند بررسی است.");
        }
        else
            bottle=await db.Bottles.SingleOrDefaultAsync(v=>v.Id==input.BottleId&&!v.IsDeleted&&v.IsActive&&v.VolumeMl>=input.Volume,ct)
                ??throw new InvalidOperationException("شیشه انتخاب‌شده معتبر نیست.");
        SalesList? reference=null;
        if(input.ReferenceId.HasValue)reference=await db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==input.ReferenceId&&!v.IsDeleted&&!v.IsInventoryOffer,ct)
            ??throw new InvalidOperationException("لیست مرجع پیدا نشد.");
        var used=(await db.SalesLists.Where(v=>v.InventoryCode!=null).Select(v=>v.InventoryCode!.Value).ToArrayAsync(ct)).ToHashSet();
        var code=Enumerable.Range(1000,9000).FirstOrDefault(v=>!used.Contains(v));
        if(code==0)throw new InvalidOperationException("همهٔ کدهای چهاررقمی موجودی استفاده شده‌اند.");
        // PublicCode remains the existing global, internal cycle code.
        var internalCode=Math.Max(1000000,await db.SalesLists.MaxAsync(v=>(int?)v.PublicCode,ct)??1000000);
        if(internalCode==int.MaxValue)throw new InvalidOperationException("محدودهٔ کد داخلی پر شده است.");
        Guid perfumeId;
        if(reference is not null)perfumeId=reference.PerfumeId;
        else {var perfume=new Perfume{Id=Guid.NewGuid(),Name=input.Name.Trim(),EnglishName=input.Name.Trim(),PricePerMl=input.PricePerMl,CreatedAt=DateTime.UtcNow};db.Perfumes.Add(perfume);perfumeId=perfume.Id;}
        var referencePrice=reference is null ? null : await InventoryReferencePricing.ReferencePriceAsync(db,reference.Id,ct);
        if(referencePrice<=0)referencePrice=null;
        var now=DateTime.UtcNow;
        var list=new SalesList{Id=input.Id,CreatedAt=now,OpenDate=now,PublicCode=internalCode+1,InventoryCode=code,
            InventoryReferenceListId=reference?.Id,InventoryReferencePricePerMl=referencePrice,InventoryCreatedByUserId=input.Actor,StoragePosition=input.Position.Trim(),
            InventoryPublicDescription=string.IsNullOrWhiteSpace(input.Description)?null:input.Description.Trim(),
            EnglishName=reference is not null && !string.IsNullOrWhiteSpace(reference.EnglishName) ? reference.EnglishName : input.Name.Trim(),PersianName=input.Name.Trim(),DisplayBrand=reference?.DisplayBrand??"",
            PerfumeId=perfumeId,PricePerMl=input.PricePerMl,TotalVolume=input.Volume,MinimumRequestVolumeMl=input.Volume,
            IsInventoryOffer=true,FixedBottleId=bottle.Id,FixedBottlePrice=input.BottlePrice,TelegramPhotoFileId=input.Photo,Status=SalesListStatus.Open};
        db.SalesLists.Add(list);db.InventoryPositionChanges.Add(new(){Id=Guid.NewGuid(),SalesListId=list.Id,Position=list.StoragePosition,AdminUserId=input.Actor,CreatedAt=now});
        await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);return list;
    }
    public Task SetPositionAsync(Guid id,string position,long actor,CancellationToken ct=default)=>SavePositionAsync(id,position,actor,false,ct);
    public Task SetPositionConfirmedAsync(Guid id,string? expected,string position,long actor,CancellationToken ct=default)=>SavePositionAsync(id,position,actor,false,ct,expected,true);
    public Task SetSoldPositionAsync(Guid id,string position,long actor,CancellationToken ct=default)=>SavePositionAsync(id,position,actor,true,ct);
    private async Task SavePositionAsync(Guid id,string position,long actor,bool includeOrdinaryLists,CancellationToken ct,string? expected=null,bool requireExpected=false)
    {
        if(actor<=0||string.IsNullOrWhiteSpace(position)||position.Trim().Length>150)throw new InvalidOperationException("جایگاه معتبر وارد کنید؛ حداکثر ۱۵۰ کاراکتر.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var list=await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").SingleOrDefaultAsync(ct);
        if(list is null)throw new InvalidOperationException("عطر پیدا نشد.");
        await db.Entry(list).ReloadAsync(ct);
        if(list.IsDeleted||(!includeOrdinaryLists&&!list.IsInventoryOffer))throw new InvalidOperationException("موجودی پیدا نشد.");
        if(requireExpected&&(list.StoragePosition??"").Trim()!=(expected??"").Trim())throw new InvalidOperationException("جایگاه این موجودی پس از پیش‌نمایش تغییر کرده است؛ دوباره جست‌وجو و جایگاه فعلی را بررسی کنید.");
        var next=position.Trim();
        if(list.StoragePosition!=next){db.InventoryPositionChanges.Add(new(){Id=Guid.NewGuid(),SalesListId=id,PreviousPosition=list.StoragePosition,Position=next,AdminUserId=actor,CreatedAt=DateTime.UtcNow});list.StoragePosition=next;list.UpdatedAt=DateTime.UtcNow;await db.SaveChangesAsync(ct);}
        await tx.CommitAsync(ct);
    }
    public async Task RejectAsync(Guid id,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var list=await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").SingleOrDefaultAsync(ct);
        if(list is null||!list.IsInventoryOffer||!list.InventoryCode.HasValue||list.IsDeleted)throw new InvalidOperationException("موجودی پیدا نشد.");
        await db.Entry(list).ReloadAsync(ct);
        if(await db.InvoiceIssuanceBatchSalesLists.AnyAsync(v=>v.SalesListId==id,ct))throw new InvalidOperationException("برای این موجودی فاکتور صادر شده است؛ از مسیر اصلاح فاکتور استفاده کنید.");
        var requests=await db.SalesListRequests.Where(v=>v.SalesListId==id&&!v.IsDeleted&&v.Status==SalesListRequestStatus.Confirmed).ToArrayAsync(ct);
        foreach(var request in requests){request.Status=SalesListRequestStatus.Cancelled;request.UpdatedAt=DateTime.UtcNow;list.ReservedVolume=Math.Max(0,list.ReservedVolume-request.VolumeMl);}
        list.Status=list.RemainingVolume>0?SalesListStatus.Open:SalesListStatus.Full;list.UpdatedAt=DateTime.UtcNow;
        db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,Channel="ReadyInventoryRefresh",EventType="InventoryRequestRejected",Recipient=id.ToString(),Payload="{}"});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public sealed record FinancialReport(int Days,DateTime FromUtc,DateTime UntilUtc,int SoldPackages,decimal SalesAmount,
        decimal ReceivedAmount,decimal OutstandingAmount,int UnsoldPackages,decimal UnsoldValue,int ReservedPackages,decimal ReservedValue);
    public async Task<FinancialReport> FinancialReportAsync(int days,CancellationToken ct=default)
    {
        if(days is not(7 or 15 or 30))throw new InvalidOperationException("بازهٔ گزارش مالی معتبر نیست.");
        var until=DateTime.UtcNow;var from=until.AddDays(-days);
        // Select items once, even if a historical order has several invoice records.
        // Invoice line amounts preserve the discount agreed at purchase time.
        var sold=await db.OrderItems.AsNoTracking().Where(v=>!v.IsDeleted&&v.SalesList!=null&&v.SalesList.IsInventoryOffer&&
            v.Order!=null&&!v.Order.IsDeleted&&v.Order.Status!=OrderStatus.Cancelled&&
            v.Order.Invoices.Any(i=>!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled&&i.Status!=InvoiceStatus.Draft&&i.IssuedAt>=from&&i.IssuedAt<=until))
            .Select(v=>new{v.LineTotal,Total=v.Order!.FinalAmount,
                Paid=v.Order.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed).Sum(p=>(decimal?)p.Amount)??0,
                IsPaid=v.Order.Invoices.Any(i=>!i.IsDeleted&&i.Status==InvoiceStatus.Paid)||v.Order.Status==OrderStatus.Paid}).ToArrayAsync(ct);
        var sales=sold.Sum(v=>v.LineTotal);
        var received=sold.Sum(v=>v.IsPaid?v.LineTotal:v.Total>0?
            decimal.Round(v.LineTotal*Math.Clamp(v.Paid,0,v.Total)/v.Total,2,MidpointRounding.AwayFromZero):0);
        // An indefinite reservation is unsold until an admin actually invoices it.
        // Stock value is all current unsold packages, independent of registration date.
        var stock=await db.SalesLists.AsNoTracking().Where(v=>!v.IsDeleted&&v.IsInventoryOffer&&
            (v.Status==SalesListStatus.Open||v.Status==SalesListStatus.Full||v.Status==SalesListStatus.QueuedForInvoice)&&
            !db.OrderItems.Any(x=>x.SalesListId==v.Id&&!x.IsDeleted&&x.Order!=null&&!x.Order.IsDeleted&&x.Order.Status!=OrderStatus.Cancelled&&
                x.Order.Invoices.Any(i=>!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled&&i.Status!=InvoiceStatus.Draft)))
            .Select(v=>new{Value=v.PricePerMl*v.TotalVolume+(v.FixedBottlePrice??0),Reserved=v.ReservedVolume>0}).ToArrayAsync(ct);
        return new(days,from,until,sold.Length,sales,received,sales-received,stock.Length,stock.Sum(v=>v.Value),
            stock.Count(v=>v.Reserved),stock.Where(v=>v.Reserved).Sum(v=>v.Value));
    }
    public sealed record Report(int RegisteredPackages,int DistinctPerfumes,int InvoicedPackages,int Invoices,int UnpaidInvoices,int UnpaidPackages,decimal Outstanding);
    public async Task<Report> ReportAsync(int days,CancellationToken ct=default)
    {
        if(days is not(10 or 20 or 30 or -30))throw new InvalidOperationException("بازه معتبر نیست.");
        var until=DateTime.UtcNow;var from=days==-30?DateTime.MinValue:until.AddDays(-days);if(days==-30)until=until.AddDays(-30);
        var registered=db.SalesLists.AsNoTracking().Where(v=>v.IsInventoryOffer&&v.InventoryCode!=null&&!v.IsDeleted&&v.CreatedAt>=from&&v.CreatedAt<until);
        var rows=await db.OrderItems.AsNoTracking().Where(v=>!v.IsDeleted&&v.SalesList!=null&&v.SalesList.InventoryCode!=null&&v.Order!=null&&!v.Order.IsDeleted&&
            v.Order.Status!=OrderStatus.Cancelled&&v.Order.Invoices.Any(i=>!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled&&i.Status!=InvoiceStatus.Draft&&i.IssuedAt>=from&&i.IssuedAt<until))
            .Select(v=>new{v.SalesListId,v.OrderId,v.LineTotal,Total=v.Order!.FinalAmount,
                Paid=v.Order.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed).Sum(p=>(decimal?)p.Amount)??0,
                IsPaid=v.Order.Invoices.Any(i=>!i.IsDeleted&&i.Status==InvoiceStatus.Paid)||v.Order.Status==OrderStatus.Paid}).ToArrayAsync(ct);
        var unpaid=rows.Where(v=>!v.IsPaid&&v.Total>v.Paid).ToArray();
        // Inventory invoices are issued independently; proportional allocation also handles legacy mixed invoices.
        return new(await registered.CountAsync(ct),await registered.Select(v=>v.PerfumeId).Distinct().CountAsync(ct),rows.Select(v=>v.SalesListId).Distinct().Count(),
            rows.Select(v=>v.OrderId).Distinct().Count(),unpaid.Select(v=>v.OrderId).Distinct().Count(),unpaid.Select(v=>v.SalesListId).Distinct().Count(),
            unpaid.Sum(v=>v.Total>0?Math.Max(0,v.Total-v.Paid)*v.LineTotal/v.Total:0));
    }
}
