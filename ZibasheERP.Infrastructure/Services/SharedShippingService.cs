using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

// A parcel explicitly authorized by an administrator. Financial ownership and
// gift recipients remain untouched; only physical parcel membership changes.
public sealed class SharedShippingService(AppDbContext db)
{
    public const string Channel = "SharedShippingParcel";
    public sealed record Parcel(Guid RequestId, Guid PrimaryCustomerId, Guid AddressId,
        Guid[] CustomerIds, long Actor, DateTime CreatedAt, DateTime? ClosedAt = null);
    public sealed record Preview(Parcel Parcel, Guid[] ItemIds, string Fingerprint);
    public static Guid ReceiptId(Guid request) => new(SHA256.HashData(Encoding.UTF8.GetBytes("shared-shipping:" + request))[..16]);

    public static async Task<Parcel?> ReadAsync(AppDbContext db, Guid request, CancellationToken ct = default)
    {
        var row = await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n => n.Id == ReceiptId(request) && !n.IsDeleted, ct);
        if (row is null) return null;
        Parcel? parcel;
        try { parcel = JsonSerializer.Deserialize<Parcel>(row.Payload); }
        catch (JsonException) { throw new InvalidOperationException("اطلاعات پست مشترک نیازمند بررسی است."); }
        if (row.Channel != Channel || parcel is null || parcel.RequestId != request || row.CustomerId != parcel.PrimaryCustomerId ||
            parcel.CustomerIds.Length is < 2 or > 20 || parcel.CustomerIds.Distinct().Count() != parcel.CustomerIds.Length ||
            !parcel.CustomerIds.Contains(parcel.PrimaryCustomerId))
            throw new InvalidOperationException("اطلاعات پست مشترک معتبر نیست؛ ارسال متوقف شد.");
        return parcel;
    }

    public static IQueryable<OrderItem> Items(AppDbContext db, Guid request) => db.OrderItems
        .Where(i => !i.IsDeleted && i.ShippingRequestId == request)
        .Include(i => i.Order).ThenInclude(o => o!.Customer).Include(i => i.SalesList)
        .Include(i => i.Perfume).Include(i => i.Bottle).Include(i => i.SourceSalesListRequest);

    public async Task<Customer> ResolveCustomerAsync(string identity, CancellationToken ct = default)
    {
        var value = InvoiceCaptionIdentity.Digits(identity.Trim());
        if (value.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase)) value = value[13..];
        var name = value.TrimStart('@').ToLowerInvariant();
        var numeric = long.TryParse(value, out var id) && id > 0;
        var customers = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted &&
            (numeric ? c.TelegramId == value : c.Username != null && c.Username.Replace("@", "").ToLower() == name)).Take(2).ToArrayAsync(ct);
        if (customers.Length != 1) throw new InvalidOperationException("مشتری یکتا پیدا نشد؛ آیدی ثبت‌شدهٔ مشتری را وارد کنید.");
        return customers[0];
    }

    public async Task<Guid> ResolvePrimaryRequestAsync(Guid primary, Guid? selectedRequest, CancellationToken ct = default)
    {
        if (selectedRequest.HasValue) return selectedRequest.Value;
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == primary && !c.IsDeleted, ct)
            ?? throw new InvalidOperationException("گیرندهٔ اصلی پیدا نشد.");
        // An address-only card may have been posted after the ready items were
        // assigned to an older parcel. Reuse that parcel; do not create a second
        // request or silently move items between multiple existing parcels.
        var requests = await ShippingPackageService.CustomerItems(db, customer).AsNoTracking()
            .Where(i => i.ShippedAt == null && i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip &&
                i.ShippingRequestId != null)
            .Select(i => i.ShippingRequestId!.Value).Distinct().Take(2).ToArrayAsync(ct);
        if (requests.Length == 0) return Guid.NewGuid();
        if (requests.Length > 1)
            throw new InvalidOperationException("گیرندهٔ اصلی چند بستهٔ باز دارد؛ آدرسِ بستهٔ موردنظر را انتخاب کنید.");
        await ShippingPackageService.ValidatePackageAsync(db, requests[0], primary, ct);
        return requests[0];
    }

    public async Task<Preview> PreviewAsync(Guid request, Guid primary, Guid addressId, Guid[] memberIds, long actor, CancellationToken ct = default)
    {
        if (request == Guid.Empty || actor <= 0 || memberIds.Length is < 1 or > 19 || memberIds.Contains(primary) || memberIds.Distinct().Count() != memberIds.Length)
            throw new InvalidOperationException("آیدی نفر اصلی و همراهان باید متفاوت و بدون تکرار باشند.");
        if(await db.TrackingDispatches.AnyAsync(d=>!d.IsDeleted&&d.ShippingRequestId==request&&
            (d.Status==TrackingDispatchStatus.Sending||d.Status==TrackingDispatchStatus.Sent||d.ImportBatch!.ConfirmedAt!=null||d.Deliveries.Any(x=>x.AttemptedAt!=null)),ct))
            throw new InvalidOperationException("ارسال رهگیری این بسته شروع شده؛ عضویت پست مشترک قابل تغییر نیست.");
        var existing = await ReadAsync(db, request, ct);
        if (existing is { ClosedAt: not null } || existing != null && (existing.PrimaryCustomerId != primary || existing.AddressId != addressId))
            throw new InvalidOperationException("این بسته بسته شده یا گیرندهٔ اصلی آن تغییر کرده است.");
        var customers = (existing?.CustomerIds ?? [primary]).Concat(memberIds).Distinct().Order().ToArray();
        if (customers.Length > 20) throw new InvalidOperationException("حداکثر ۲۰ مشتری می‌توانند در یک بسته باشند.");
        if (!await db.Addresses.AsNoTracking().AnyAsync(a => a.Id == addressId && a.CustomerId == primary && !a.IsDeleted, ct))
            throw new InvalidOperationException("آدرس ثبت‌شده متعلق به گیرندهٔ اصلی نیست.");
        var assigned = await Items(db, request).AsNoTracking().ToArrayAsync(ct);
        if (assigned.Any(i => i.ShippedAt != null || i.FulfillmentStatus == OrderItemFulfillmentStatus.Shipped))
            throw new InvalidOperationException("پست ارسال‌شده قابل تغییر نیست.");
        if (assigned.Length > 0) await ShippingPackageService.ValidatePackageAsync(db, request, primary, ct);
        var items = assigned.ToList();
        foreach (var customerId in customers)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct)
                ?? throw new InvalidOperationException("یکی از مشتری‌ها دیگر فعال نیست.");
            var ready = await ShippingPackageService.CustomerItems(db, customer).AsNoTracking().Include(i=>i.Order).Include(i=>i.SourceSalesListRequest).Where(i => i.ShippedAt == null &&
                i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip).ToArrayAsync(ct);
            if (ready.Any(i => i.ShippingRequestId != null && i.ShippingRequestId != request))
                throw new InvalidOperationException($"{InvoiceTransferService.Label(customer)} بستهٔ باز دیگری دارد؛ ابتدا عضویت آن بسته را تعیین تکلیف کنید.");
            if (customerId != primary && ready.Length == 0) throw new InvalidOperationException($"{InvoiceTransferService.Label(customer)} آیتم آمادهٔ ارسال ندارد.");
            items.AddRange(ready.Where(i => i.ShippingRequestId == null));
        }
        var all = items.DistinctBy(i => i.Id).OrderBy(i => i.Id).ToArray();
        if (all.Length == 0 || all.Any(i => i.FulfillmentStatus != OrderItemFulfillmentStatus.DecantedReadyToShip || i.ShippedAt != null))
            throw new InvalidOperationException("این بسته باید فقط شامل آیتم‌های آمادهٔ ارسال باشد.");
        var parcel = new Parcel(request, primary, addressId, customers, actor, existing?.CreatedAt ?? DateTime.UtcNow);
        var address = await db.Addresses.AsNoTracking().SingleAsync(a => a.Id == addressId, ct);
        var signature = JsonSerializer.Serialize(new { request, primary, addressId, address.UpdatedAt, address.ReceiverName, address.FullAddress, address.Mobile, address.PostalCode, address.City, customers,
            Existing = existing, Items = all.Select(i => new { i.Id, i.UpdatedAt, i.OrderId, i.ShippingRequestId, i.RequestedVolumeMl,
                i.FulfillmentStatus, i.ShippedAt, OrderOwner = i.Order!.CustomerId, RequestVersion = i.SourceSalesListRequest?.RowVersion }) });
        return new(parcel, all.Select(i => i.Id).ToArray(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))));
    }

    public async Task<Parcel> ConfirmAsync(Preview preview, long actor, long addressMessageId, CancellationToken ct = default, Guid? addressReferenceId = null)
    {
        if (actor != preview.Parcel.Actor || addressMessageId <= 0 || db.ChangeTracker.HasChanges()) throw new InvalidOperationException("درخواست پست مشترک معتبر نیست.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var request = preview.Parcel.RequestId;
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"shared-shipping:" + request},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Shared parcel busy',1;", ct);
        foreach (var id in preview.Parcel.CustomerIds.Order())
            await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        await db.Addresses.FromSqlInterpolated($"SELECT * FROM Addresses WITH(HOLDLOCK) WHERE Id={preview.Parcel.AddressId}").AsNoTracking().SingleAsync(ct);
        var lockedItems = new List<OrderItem>();
        foreach(var id in preview.ItemIds.Order())
            lockedItems.Add(await db.OrderItems.FromSqlInterpolated($"SELECT * FROM OrderItems WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct));
        foreach(var id in lockedItems.Where(i=>i.SourceSalesListRequestId!=null).Select(i=>i.SourceSalesListRequestId!.Value).Distinct().Order())
            await db.SalesListRequests.FromSqlInterpolated($"SELECT * FROM SalesListRequests WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        db.ChangeTracker.Clear();
        var fresh = await PreviewAsync(request, preview.Parcel.PrimaryCustomerId, preview.Parcel.AddressId,
            preview.Parcel.CustomerIds.Where(id => id != preview.Parcel.PrimaryCustomerId).ToArray(), actor, ct);
        if (fresh.Fingerprint != preview.Fingerprint) throw new InvalidOperationException("اقلام، آدرس یا عضویت بسته تغییر کرده؛ پیش‌نمایش تازه بگیرید.");
        var now = DateTime.UtcNow;
        var since = await db.OrderItems.Where(i => i.ShippingRequestId == request && i.ShippingRequestedAt != null).MinAsync(i => i.ShippingRequestedAt, ct) ?? now;
        var items = await db.OrderItems.Include(i => i.Order).Where(i => fresh.ItemIds.Contains(i.Id)).ToArrayAsync(ct);
        foreach (var item in items)
        {
            item.ShippingRequestId = request; item.ShippingRequestedAt = since; item.ShippingTelegramMessageId = addressMessageId; item.UpdatedAt = now;
            // The order's financial owner is unchanged; only its dispatch address follows this parcel.
            item.Order!.DeliveryAddressId = fresh.Parcel.AddressId; item.Order.UpdatedAt = now;
        }
        var receipt = await db.NotificationOutbox.SingleOrDefaultAsync(n => n.Id == ReceiptId(request), ct);
        if (receipt is null) db.NotificationOutbox.Add(new() { Id=ReceiptId(request), CustomerId=fresh.Parcel.PrimaryCustomerId,
            Channel=Channel, EventType="AdminSharedParcel", Recipient=request.ToString("N"), Payload=JsonSerializer.Serialize(fresh.Parcel),
            Status=NotificationOutboxStatus.Processed, CreatedAt=now, ProcessedAt=now });
        else { receipt.Payload=JsonSerializer.Serialize(fresh.Parcel); receipt.UpdatedAt=now; }
        if(addressReferenceId.HasValue)
        {
            var reference=await db.NotificationOutbox.SingleAsync(n=>n.Id==addressReferenceId&&!n.IsDeleted&&n.Channel=="ShippingReferenceContext",ct);
            var data=System.Text.Json.Nodes.JsonNode.Parse(reference.Payload)!.AsObject();
            if(reference.CustomerId!=fresh.Parcel.PrimaryCustomerId||data["CustomerId"]?.GetValue<Guid>()!=fresh.Parcel.PrimaryCustomerId||
                data["AddressId"]?.GetValue<Guid>()!=fresh.Parcel.AddressId||data["MessageId"]?.GetValue<long>()!=addressMessageId||
                (data["RequestId"]!=null&&data["RequestId"]!.GetValue<Guid>()!=request)||data["Kind"]?.GetValue<string>()!="root"||data["Installed"]?.GetValue<bool>()!=true)
                throw new InvalidOperationException("مرجع آدرس تغییر کرده؛ دوباره شروع کنید.");
            data["RequestId"]=JsonSerializer.SerializeToNode(request);data["ItemIds"]=JsonSerializer.SerializeToNode(fresh.ItemIds);
            reference.Payload=data.ToJsonString();reference.UpdatedAt=now;
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=fresh.Parcel.PrimaryCustomerId,Channel="SharedShippingRefresh",
                EventType="RefreshSharedParcelAddress",Recipient=reference.Recipient,Payload=JsonSerializer.Serialize(new{ReferenceId=reference.Id}),CreatedAt=now});
        }
        await db.SaveChangesAsync(ct);
        await ShippingPackageService.ValidatePackageAsync(db, request, fresh.Parcel.PrimaryCustomerId, ct);
        await tx.CommitAsync(ct); return fresh.Parcel;
    }
}
