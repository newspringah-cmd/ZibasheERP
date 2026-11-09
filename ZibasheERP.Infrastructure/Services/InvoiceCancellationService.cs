using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class InvoiceCancellationService(AppDbContext db)
{
    public sealed record Line(Guid ItemId,string Name,decimal Volume,decimal InvoicePrice,decimal CurrentPrice,decimal BottlePrice,string BottleName,
        Guid? ReferenceId,Guid? BottleId,string Photo,string Position,string? Description);
    public sealed record Preview(Guid InvoiceId,string Number,string Customer,decimal Amount,string Fingerprint,Line[] Lines,
        Guid[]? SelectedItems=null,decimal RemainingAmount=0,bool Partial=false);
    public sealed record Result(Guid InvoiceId,decimal ReversedAmount,Guid[] Offers,int[] Codes,bool Returned,
        Guid? SourceInvoiceId=null,Guid[]? SelectedItems=null,decimal RemainingAmount=0,string? Fingerprint=null);
    private IQueryable<Invoice> Query()=>db.Invoices.Include(i=>i.Order).ThenInclude(o=>o!.Customer)
        .Include(i=>i.Order).ThenInclude(o=>o!.Payments).Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.SalesList).ThenInclude(l=>l!.Perfume)
        .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.Bottle)
        .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.Perfume)
        .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.SourceSalesListRequest);
    public async Task<Preview> ResolveAsync(string caption,bool returned,CancellationToken ct=default)
        =>await BuildAsync(await ResolveInvoiceAsync(caption,ct),returned,ct);
    public async Task<Preview> ResolveForSelectionAsync(string caption,bool returned,CancellationToken ct=default)
        =>await BuildAsync(await ResolveInvoiceAsync(caption,ct),returned,ct,validateReturn:false);
    public async Task<Preview> SelectAsync(Preview original,bool returned,Guid[] selected,CancellationToken ct=default)
    {
        var invoice=await Query().AsNoTracking().SingleAsync(i=>i.Id==original.InvoiceId,ct);
        var fresh=await BuildAsync(invoice,returned,ct,validateReturn:false);
        if(fresh.Fingerprint!=original.Fingerprint)throw new InvalidOperationException("فاکتور پس از نمایش آیتم‌ها تغییر کرده؛ کپشن تازه را دوباره بفرستید.");
        return await BuildAsync(invoice,returned,ct,selected);
    }
    public async Task<Guid> ResolveIdentityAsync(string caption,CancellationToken ct=default)=>(await ResolveInvoiceAsync(caption,ct)).Id;
    private async Task<Invoice> ResolveInvoiceAsync(string caption,CancellationToken ct)
    {
        var key=InvoiceCaptionIdentity.Parse(caption);
        var q=Query().AsNoTracking().Where(i=>!i.IsDeleted&&i.Order!=null&&!i.Order.IsDeleted&&i.TotalAmount==key.Amount);
        if(key.Username is not null)q=q.Where(i=>i.Order!.Customer!.Username!=null&&i.Order.Customer.Username.Replace("@","").ToLower()==key.Username);
        if(key.Number is not null)q=q.Where(i=>i.InvoiceNumber==key.Number);
        else q=q.Where(i=>i.IssuedAt>=key.FromUtc&&i.IssuedAt<key.UntilUtc);
        if(key.Inventory)q=q.Where(i=>i.Order!.IsInventory);
        var candidates=await q.Take(30).ToArrayAsync(ct);
        candidates=candidates.Where(i=>Matches(i,key)).ToArray();
        if(candidates.Length!=1)throw new InvalidOperationException(candidates.Length==0?"فاکتور مطابق این کپشن پیدا نشد؛ کپشن اصلی و کامل را بفرستید.":"چند فاکتور مشابه پیدا شد؛ کپشن دارای شمارهٔ فاکتور را بفرستید تا اشتباهی لغو نشود.");
        return candidates[0];
    }
    private static bool Matches(Invoice i,InvoiceCaptionIdentity key)
    {
        var items=i.Order!.Items.Where(x=>!x.IsDeleted||i.Status==InvoiceStatus.Cancelled).ToArray();
        if(key.Names.Length>0)
        {
            if(key.Names.Length!=items.Length)return false;
            var unmatched=items.ToList();
            foreach(var name in key.Names)
            {
                var index=unmatched.FindIndex(x=>new[]{x.SalesList?.PersianName,x.SalesList?.EnglishName,x.Perfume?.Name,x.Perfume?.EnglishName,x.ManualDescription}.Any(s=>s!=null&&InvoiceCaptionIdentity.Normalize(s)==name));
                if(index<0)return false;unmatched.RemoveAt(index);
            }
        }
        return key.Volumes.Length==0||key.Volumes.Order().SequenceEqual(items.Select(x=>x.RequestedVolumeMl).Order());
    }
    private static void Validate(Invoice invoice)
    {
        var o=invoice.Order??throw new InvalidOperationException("سفارش فاکتور پیدا نشد.");
        if(invoice.IsDeleted||o.IsDeleted||invoice.Status is InvoiceStatus.Cancelled or InvoiceStatus.Draft||o.Status==OrderStatus.Cancelled)throw new InvalidOperationException("فاکتور فعال نیست یا قبلاً لغو شده است.");
        if(invoice.Status==InvoiceStatus.Paid||o.Status==OrderStatus.Paid||o.Payments.Any(p=>!p.IsDeleted&&p.Status is PaymentStatus.Confirmed or PaymentStatus.Refunded))throw new InvalidOperationException("این فاکتور پرداخت یا استرداد دارد؛ ابتدا حسابداری باید پرداخت را رسیدگی کند.");
        if(o.Status is OrderStatus.Shipped or OrderStatus.Delivered||o.ShippedAt!=null||o.Items.Any(x=>!x.IsDeleted&&(x.ShippedAt!=null||x.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped)))throw new InvalidOperationException("فاکتور دارای آیتم ارسال‌شده قابل لغو یا برگشت مستقیم نیست.");
        if(invoice.TotalAmount<=0||invoice.TotalAmount!=o.FinalAmount||o.Items.Where(x=>!x.IsDeleted).Sum(x=>x.LineTotal)!=invoice.TotalAmount)throw new InvalidOperationException("مبالغ فاکتور و آیتم‌ها تطبیق ندارند؛ بررسی حسابداری لازم است.");
    }
    private async Task<Preview> BuildAsync(Invoice invoice,bool returned,CancellationToken ct,Guid[]? selected=null,bool validateReturn=true)
    {
        Validate(invoice);var order=invoice.Order!;var lines=new List<Line>();
        if(await db.NotificationOutbox.AnyAsync(n=>!n.IsDeleted&&n.OrderId==order.Id&&n.Channel==InvoiceTransferService.WorkChannel&&n.Status!=NotificationOutboxStatus.Processed,ct))
            throw new InvalidOperationException("انتقال این فاکتور هنوز تکمیل نشده؛ ابتدا وضعیت ارسال و پاک‌سازی انتقال را بررسی کنید.");
        if(await db.NotificationOutbox.AnyAsync(n=>!n.IsDeleted&&n.OrderId==order.Id&&n.Channel==InvoiceRevisionService.Channel&&n.Status!=NotificationOutboxStatus.Processed,ct))
            throw new InvalidOperationException("اصلاح قبلی فاکتور هنوز در حال ارسال یا پاک‌سازی است؛ ابتدا وضعیت آن را بررسی کنید.");
        var active=order.Items.Where(x=>!x.IsDeleted).ToArray();
        if(selected is not null&&(selected.Length==0||selected.Distinct().Count()!=selected.Length||selected.Except(active.Select(x=>x.Id)).Any()))
            throw new InvalidOperationException("حداقل یک آیتم معتبر از همین فاکتور انتخاب کنید.");
        var requests=order.Items.Where(x=>!x.IsDeleted&&x.ShippingRequestId!=null).Select(x=>x.ShippingRequestId!.Value).ToArray();
        if(await db.TrackingDispatches.AnyAsync(d=>!d.IsDeleted&&d.ShippingRequestId!=null&&requests.Contains(d.ShippingRequestId.Value)&&(d.Status==TrackingDispatchStatus.Sending||d.Status==TrackingDispatchStatus.Sent),ct))throw new InvalidOperationException("ارسال یا کد رهگیری این بسته شروع شده؛ ابتدا وضعیت ارسال باید بررسی شود.");
        foreach(var x in order.Items.Where(x=>!x.IsDeleted).OrderBy(x=>x.RowNumber).ThenBy(x=>x.Id))
        {
            var source=x.SalesList;var reference=source?.IsInventoryOffer==true?source.InventoryReferenceListId:source?.Id;
            if(reference is null&&x.PerfumeId!=null)reference=await db.SalesLists.AsNoTracking().Where(l=>!l.IsDeleted&&!l.IsInventoryOffer&&l.PerfumeId==x.PerfumeId&&l.TelegramPhotoFileId!=null).OrderByDescending(l=>l.OpenDate).Select(l=>(Guid?)l.Id).FirstOrDefaultAsync(ct);
            var price=reference.HasValue?await InventoryReferencePricing.ReferencePriceAsync(db,reference.Value,ct):null;
            price=price>0?price:x.Perfume?.PricePerMl>0?x.Perfume.PricePerMl:source?.PricePerMl;
            var photo=source?.TelegramPhotoFileId;
            if(string.IsNullOrWhiteSpace(photo)&&source!=null)photo=await db.TelegramSalesListImports.AsNoTracking().Where(p=>p.SalesListId==source.Id&&p.TelegramPhotoFileId!=null).OrderByDescending(p=>p.CreatedAt).Select(p=>p.TelegramPhotoFileId).FirstOrDefaultAsync(ct);
            if(string.IsNullOrWhiteSpace(photo)&&reference!=null)photo=await db.SalesLists.AsNoTracking().Where(l=>l.Id==reference).Select(l=>l.TelegramPhotoFileId).FirstOrDefaultAsync(ct);
            var returning=returned&&validateReturn&&(selected is null||selected.Contains(x.Id));
            if(returning&&(price is not >0||string.IsNullOrWhiteSpace(photo)||x.Bottle is not {IsDeleted:false,IsActive:true}||x.Bottle.VolumeMl<x.RequestedVolumeMl||x.RequestedVolumeMl is <0.5m or >1000||x.RequestedVolumeMl*2!=decimal.Truncate(x.RequestedVolumeMl*2)))throw new InvalidOperationException("عکس، قیمت روز، حجم نیم‌میل یا شیشهٔ مناسبِ یکی از آیتم‌های انتخاب‌شده کامل نیست؛ پیش از انتقال به موجودی اصلاح کنید. هنوز فاکتور لغو نشده است.");
            if(returning&&await db.SalesLists.AnyAsync(l=>l.SourceOrderItemId==x.Id,ct))throw new InvalidOperationException("یکی از آیتم‌ها قبلاً به موجودی منتقل شده است.");
            lines.Add(new(x.Id,source?.PersianName is {Length:>0}?source.PersianName:x.Perfume?.Name??source?.EnglishName??x.ManualDescription??"عطر",x.RequestedVolumeMl,x.PerfumePricePerMl,price??0,x.BottlePrice,x.Bottle?.Name??"ثبت نشده",reference,x.BottleId,photo??"",source?.StoragePosition??"جایگاه ثبت نشده",source?.InventoryPublicDescription));
        }
        var data=JsonSerializer.Serialize(new{invoice.Id,InvoiceStatus=invoice.Status,invoice.TotalAmount,OrderStatus=order.Status,order.FinalAmount,Lines=lines,
            Items=order.Items.Where(x=>!x.IsDeleted).OrderBy(x=>x.Id).Select(x=>new{x.Id,x.LineTotal,x.FulfillmentStatus,x.ShippedAt,x.ShippingRequestId,x.SourceSalesListRequestId,RequestVersion=x.SourceSalesListRequest?.RowVersion}),Returned=returned,Selected=selected?.Order().ToArray()});
        var customer=order.Customer!;var identity=string.IsNullOrWhiteSpace(customer.Username)?$"{customer.FullName} ({customer.TelegramId??customer.Id.ToString("N")})":"@"+customer.Username.TrimStart('@');
        var amount=active.Where(x=>selected is null||selected.Contains(x.Id)).Sum(x=>x.LineTotal);
        if(amount<=0)throw new InvalidOperationException("مبلغ آیتم‌های انتخاب‌شده باید مثبت باشد.");
        return new(invoice.Id,invoice.InvoiceNumber,identity,amount,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))),
            lines.Where(x=>selected is null||selected.Contains(x.ItemId)).ToArray(),selected,invoice.TotalAmount-amount,selected is not null&&selected.Length<active.Length);
    }
    public async Task<Result> ExecuteAsync(Guid operation,Preview expected,bool returned,IReadOnlyDictionary<Guid,decimal> prices,long actor,string inventoryChannel,CancellationToken ct=default)
    {
        if(operation==Guid.Empty||actor<=0||returned&&string.IsNullOrWhiteSpace(inventoryChannel))throw new InvalidOperationException("درخواست یا مقصد موجودی معتبر نیست.");
        if(db.ChangeTracker.HasChanges())throw new InvalidOperationException("ابتدا تغییرات قبلی را تکمیل و کپشن را دوباره بررسی کنید.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"invoice-cancel:"+expected.InvoiceId},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Invoice cancellation busy',1;",ct);
        var prior=await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==operation,ct);
        if(prior is not null)
        {
            if(prior.Channel!="InvoiceCancellationReceipt")throw new InvalidOperationException("شناسهٔ عملیات تکراری نامعتبر است.");
            var saved=JsonSerializer.Deserialize<Result>(prior.Payload)!;
            if((saved.SourceInvoiceId??saved.InvoiceId)!=expected.InvoiceId||saved.Returned!=returned||saved.Fingerprint is not null&&saved.Fingerprint!=expected.Fingerprint)throw new InvalidOperationException("درخواست تکراری نامعتبر است.");
            await tx.CommitAsync(ct);return saved;
        }
        if(returned)await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sp_getapplock @Resource='ready-inventory-code',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Inventory code allocation busy',1;",ct);
        var initial=await db.Invoices.AsNoTracking().Where(i=>i.Id==expected.InvoiceId).Select(i=>new{i.OrderId,CustomerId=i.Order!.CustomerId}).SingleAsync(ct);
        var listIds=await db.OrderItems.AsNoTracking().Where(x=>x.OrderId==initial.OrderId&&!x.IsDeleted&&x.SalesListId!=null).Select(x=>x.SalesListId!.Value).Distinct().ToArrayAsync(ct);
        foreach(var id in listIds.Order())await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={initial.CustomerId}").AsNoTracking().SingleAsync(ct);
        await db.Orders.FromSqlInterpolated($"SELECT * FROM Orders WITH(UPDLOCK,HOLDLOCK) WHERE Id={initial.OrderId}").AsNoTracking().SingleAsync(ct);
        await db.Invoices.FromSqlInterpolated($"SELECT * FROM Invoices WITH(UPDLOCK,HOLDLOCK) WHERE Id={expected.InvoiceId}").AsNoTracking().SingleAsync(ct);
        await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WITH(UPDLOCK,HOLDLOCK) WHERE OrderId={initial.OrderId}").AsNoTracking().ToArrayAsync(ct);
        // This is a standalone financial unit of work. Discard only unchanged cached
        // entities after taking locks so preview/payment changes cannot remain stale.
        db.ChangeTracker.Clear();
        var invoice=await Query().SingleAsync(i=>i.Id==expected.InvoiceId,ct);var fresh=await BuildAsync(invoice,returned,ct,expected.SelectedItems);
        if(fresh.Fingerprint!=expected.Fingerprint)throw new InvalidOperationException("فاکتور، وضعیت آیتم یا قیمت روز پس از پیش‌نمایش تغییر کرده؛ کپشن را دوباره بررسی کنید.");
        var o=invoice.Order!;if(o.Customer!.CurrentDebt<invoice.TotalAmount)throw new InvalidOperationException("ماندهٔ حساب با مبلغ فاکتور تطبیق ندارد؛ اصلاح خودکار متوقف شد.");
        if(await db.Invoices.AnyAsync(i=>i.OrderId==o.Id&&i.Id!=invoice.Id&&!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled&&i.Status!=InvoiceStatus.Draft,ct))throw new InvalidOperationException("سفارش بیش از یک فاکتور فعال دارد؛ بررسی حسابداری لازم است.");
        var pending=await db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE OrderId={o.Id} AND IsDeleted=0 AND Channel IN(N'Telegram',N'N8n') AND Status<>{NotificationOutboxStatus.Processed}").ToArrayAsync(ct);
        var now=DateTime.UtcNow;if(pending.Any(n=>n.Status==NotificationOutboxStatus.Processing&&n.LockedUntil>now))throw new InvalidOperationException("ارسال یکی از پیام‌های فاکتور در حال انجام است؛ کمی بعد دوباره تلاش کنید.");
        Guid? sourceInvoice=null;
        if(fresh.Partial)
        {
            sourceInvoice=invoice.Id;
            invoice=await InvoiceRevisionService.SplitAsync(db,invoice,fresh.Lines.Select(l=>l.ItemId).ToArray(),operation,now,ct);
            o=invoice.Order!;
        }
        var offers=new List<SalesList>();
        if(returned)
        {
            if(prices.Count!=fresh.Lines.Length||fresh.Lines.Any(l=>!prices.TryGetValue(l.ItemId,out var p)||p<=0||p>1000000000000m||decimal.Round(p,2)!=p))throw new InvalidOperationException("قیمت هر میل همهٔ آیتم‌ها باید مشخص و مثبت باشد.");
            foreach(var line in fresh.Lines)
            {
                var offer=await new ReadyInventoryService(db).CreateAsync(new(Guid.NewGuid(),line.ReferenceId,line.Name,line.Photo,prices[line.ItemId],line.Volume,line.BottleId!.Value,line.BottlePrice,line.Position,actor,line.Description),ct);
                offer.SourceOrderItemId=line.ItemId;
                offers.Add(offer);
                db.NotificationOutbox.Add(new(){Id=offer.Id,CustomerId=Guid.Empty,Channel="MarketingPromotion",EventType="ReadyInventoryPublication",Recipient=actor.ToString(),CreatedAt=now,
                    Payload=JsonSerializer.Serialize(new{SalesListId=offer.Id,Code=offer.InventoryCode!.Value,Destination="inventory-publish",AdminChatId=actor,AdminUserId=actor,HoldHours=24,ChannelMode="replace"})});
            }
        }
        foreach(var x in o.Items.Where(x=>!x.IsDeleted))
        {
            x.IsDeleted=true;x.UpdatedAt=now;x.ShippingRequestId=null;x.ShippingRequestedAt=null;x.ShippingTelegramMessageId=null;
            if(x.SourceSalesListRequest is {IsDeleted:false} request){request.Status=SalesListRequestStatus.Cancelled;request.UpdatedAt=now;}
            if(x.SalesList?.IsInventoryOffer==true){x.SalesList.Status=SalesListStatus.Cancelled;x.SalesList.UpdatedAt=now;}
        }
        foreach(var p in o.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Pending)){p.Status=PaymentStatus.Cancelled;p.IsSuccessful=false;p.UpdatedAt=now;}
        foreach(var n in pending.Where(n=>sourceInvoice is null||n.IsDeleted)){n.Status=NotificationOutboxStatus.Processed;n.ProcessedAt=now;n.LockedUntil=null;n.NextAttemptAt=null;n.LastError="Suppressed: invoice cancelled";}
        o.Customer!.CurrentDebt-=invoice.TotalAmount;o.Customer.UpdatedAt=now;o.Status=OrderStatus.Cancelled;o.CancelledAt=now;o.CancelReason=$"Invoice cancelled by Telegram admin {actor}; return to inventory: {returned}";o.UpdatedAt=now;
        invoice.Status=InvoiceStatus.Cancelled;invoice.UpdatedAt=now;invoice.DeliveryStatus=InvoiceDeliveryStatus.Cancelled;invoice.DeliveryStatusChangedAt=now;invoice.DeliveryStatusNote="Cancelled by accounting; financial history retained";
        await InvoiceMessageLifecycle.QueueKnownAsync(db,invoice,ct);
        var result=new Result(invoice.Id,invoice.TotalAmount,offers.Select(l=>l.Id).ToArray(),offers.Select(l=>l.InventoryCode!.Value).ToArray(),returned,
            sourceInvoice,fresh.Lines.Select(l=>l.ItemId).ToArray(),fresh.RemainingAmount,fresh.Fingerprint);
        db.NotificationOutbox.Add(new(){Id=operation,CustomerId=o.CustomerId,OrderId=o.Id,Channel="InvoiceCancellationReceipt",EventType=returned?"InvoiceCancelledAndRestocked":"InvoiceCancelled",Recipient=actor.ToString(),Payload=JsonSerializer.Serialize(result),Status=NotificationOutboxStatus.Processed,CreatedAt=now,ProcessedAt=now});
        db.NotificationOutbox.Add(InvoiceRestockTracking.RefreshJob(invoice,o.CustomerId,now));
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
    }
}
