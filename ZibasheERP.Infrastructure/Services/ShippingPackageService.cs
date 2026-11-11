using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class ShippingPackageService
{
    public static IQueryable<OrderItem> CustomerItems(AppDbContext db, Customer customer) =>
        ShippingRecipientPolicy.ItemsForCustomer(db.OrderItems, db.NotificationOutbox, customer.Id, customer.TelegramId, customer.Username, db.Customers);

    public static async Task<Customer> ValidatePackageAsync(AppDbContext db, Guid requestId, Guid? expectedCustomerId = null, CancellationToken ct = default)
    {
        Customer customer;
        try { customer = await PackageCustomerAsync(db, requestId, ct); }
        catch (InvalidOperationException) { throw new ShippingRecipientValidationException(requestId, [], "گیرندهٔ یکتای بسته قابل تأیید نیست؛ گزارش و ارسال تا بررسی اطلاعات مشتری متوقف شد."); }
        if (expectedCustomerId.HasValue && expectedCustomerId != customer.Id)
            throw new ShippingRecipientValidationException(requestId, [], "گیرندهٔ کد رهگیری با گیرندهٔ بسته یکسان نیست.");
        var actual = await db.OrderItems.AsNoTracking().Where(i => !i.IsDeleted && i.ShippingRequestId == requestId).Select(i => i.Id).ToArrayAsync(ct);
        var shared = await SharedShippingService.ReadAsync(db, requestId, ct);
        var owners = shared?.CustomerIds ?? [customer.Id];
        var owned = new List<Guid>();
        foreach(var ownerId in owners)
        {
            var owner = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==ownerId&&!c.IsDeleted,ct)
                ?? throw new ShippingRecipientValidationException(requestId,[],"یکی از اعضای پست مشترک دیگر فعال نیست.");
            var memberItems=await CustomerItems(db,owner).AsNoTracking().Where(i=>i.ShippingRequestId==requestId).Select(i=>i.Id).ToArrayAsync(ct);
            if(memberItems.Intersect(owned).Any())throw new ShippingRecipientValidationException(requestId,memberItems,"مالکیت یکی از اقلام پست مشترک مبهم است.");
            owned.AddRange(memberItems);
        }
        var invalid = actual.Except(owned).ToArray();
        if (invalid.Length != 0) throw new ShippingRecipientValidationException(requestId, invalid,
            $"این بسته {invalid.Length} آیتم با گیرندهٔ نامشخص، متناقض یا متفاوت دارد؛ گزارش قطعی و ارسال متوقف شد. ابتدا گیرنده و عضویت بسته بررسی شود.");
        return customer;
    }

    // Customer photo feedback is informational. An authorized operator may send
    // tracking regardless of photo approval; physical recipient checks still apply.
    public static Task<Customer> ValidateDispatchAsync(AppDbContext db, Guid requestId, Guid? expectedCustomerId = null, CancellationToken ct = default) =>
        ValidatePackageAsync(db, requestId, expectedCustomerId, ct);

    public static async Task<Customer> PackageCustomerAsync(AppDbContext db, Guid requestId, CancellationToken ct = default)
    {
        var shared = await SharedShippingService.ReadAsync(db,requestId,ct);
        if(shared!=null)return await db.Customers.AsNoTracking().SingleAsync(c=>c.Id==shared.PrimaryCustomerId&&!c.IsDeleted,ct);
        var members = await db.OrderItems.AsNoTracking().Include(i => i.Order).Include(i => i.SourceSalesListRequest)
            .Where(i => !i.IsDeleted && i.ShippingRequestId == requestId && i.Order != null && !i.Order.IsDeleted && i.Order.Status != OrderStatus.Cancelled).ToArrayAsync(ct);
        if (members.Length == 0) throw new InvalidOperationException("اقلام این درخواست ارسال پیدا نشد.");
        var orderIds = members.Select(i => i.OrderId).Distinct().ToArray();
        var manualGifts = await db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.Channel == "Telegram" && n.OrderId != null && orderIds.Contains(n.OrderId.Value) &&
            (n.EventType == "GiftInvoiceIssued" || n.EventType == "InvoiceGiftDeliveryRequiresManualAction"))
            .Select(n => new { OrderId = n.OrderId!.Value, n.CustomerId }).ToArrayAsync(ct);
        var giftOrders = manualGifts.Select(n => n.OrderId).ToHashSet();
        var anchors = members.Where(i => i.SourceSalesListRequest?.IsGift != true &&
                !(i.Order!.Source == OrderSource.ManualInvoice && (giftOrders.Contains(i.OrderId) || i.Order.Notes?.Contains(ShippingRecipientPolicy.ManualGiftMarker) == true)))
            .Select(i => i.Order!.CustomerId).Distinct().ToArray();
        if (anchors.Length == 1) return await db.Customers.AsNoTracking().SingleAsync(c => c.Id == anchors[0] && !c.IsDeleted, ct);
        if (anchors.Length > 1) throw new InvalidOperationException("بستهٔ ارسال بیش از یک گیرنده دارد؛ بررسی لازم است.");
        var ids = manualGifts.Select(n => n.CustomerId).Distinct().ToArray();
        var telegramIds = members.Select(i => i.SourceSalesListRequest?.GiftRecipientTelegramUserId?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        var names = members.Select(i => i.SourceSalesListRequest?.GiftRecipientTelegramUsername?.Trim().TrimStart('@').ToLowerInvariant()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        var candidates = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted &&
            (ids.Contains(c.Id) || (c.TelegramId != null && telegramIds.Contains(c.TelegramId.Trim())) ||
             (c.Username != null && names.Contains(c.Username.Trim().Replace("@", "").ToLower())))).ToArrayAsync(ct);
        var matches = new List<Customer>();
        foreach (var customer in candidates)
            if (await CustomerItems(db, customer).CountAsync(i => i.ShippingRequestId == requestId, ct) == members.Length) matches.Add(customer);
        if (matches.Count != 1) throw new InvalidOperationException("گیرندهٔ یکتای این بسته قابل تأیید نیست؛ بررسی لازم است.");
        return matches[0];
    }

    // Billing and dispatch share this customer lock. A package that has begun
    // shipping can never acquire a later invoice through a delayed tracking send.
    private static async Task LockCustomer(AppDbContext db, Guid customerId, CancellationToken ct) =>
        await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={customerId}")
            .AsNoTracking().SingleAsync(ct);

    private static async Task<Guid[]> OpenPackages(AppDbContext db, Guid customerId, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        var rows = await CustomerItems(db, customer).AsNoTracking().Where(i => i.ShippingRequestId != null)
            .Select(i => new { Request = i.ShippingRequestId!.Value, i.FulfillmentStatus, i.ShippedAt }).ToArrayAsync(ct);
        var candidates = rows.GroupBy(i => i.Request).Where(g => g.Any(i => i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip) &&
            g.All(i => i.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped && i.ShippedAt == null)).Select(g => g.Key).ToArray();
        var sharedRows=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==SharedShippingService.Channel&&n.Payload.Contains(customerId.ToString())).Select(n=>n.Payload).ToArrayAsync(ct);
        var explicitRequests=new List<Guid>();
        foreach(var payload in sharedRows)
        {
            var parcel=System.Text.Json.JsonSerializer.Deserialize<SharedShippingService.Parcel>(payload);
            if(parcel is null||parcel.ClosedAt!=null||!parcel.CustomerIds.Contains(customerId))continue;
            var states=await SharedShippingService.Items(db,parcel.RequestId).AsNoTracking().Select(i=>new{i.FulfillmentStatus,i.ShippedAt}).ToArrayAsync(ct);
            if((states.Length==0||states.Any(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip))&&states.All(i=>i.FulfillmentStatus!=OrderItemFulfillmentStatus.Shipped&&i.ShippedAt==null))explicitRequests.Add(parcel.RequestId);
        }
        var valid = new List<Guid>();
        foreach (var request in candidates.Concat(explicitRequests).Distinct())
        {
            try {
                var primary=await ValidatePackageAsync(db,request,null,ct);
                var shared=await SharedShippingService.ReadAsync(db,request,ct);
                if(shared?.ClosedAt is null&&(primary.Id==customerId||shared?.CustomerIds.Contains(customerId)==true))valid.Add(request);
            }
            catch (InvalidOperationException) { /* Ambiguous old memberships must not acquire new items. */ }
        }
        return valid.ToArray();
    }

    public static async Task AttachInventoryAsync(AppDbContext db, Guid customerId, CancellationToken ct = default)
    {
        await using var own = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockCustomer(db, customerId, ct);
        await AttachLocked(db, customerId, ct);
        await db.SaveChangesAsync(ct);
        if (own != null) await own.CommitAsync(ct);
    }

    private static async Task AttachLocked(AppDbContext db, Guid customerId, CancellationToken ct)
    {
        var packages = await OpenPackages(db, customerId, ct);
        if (packages.Length != 1) return; // Multiple parcels require an explicit choice; never guess.
        var requestId = packages[0];
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        var anchors = await CustomerItems(db, customer).AsNoTracking().Include(i => i.Order)
            .Where(i => i.ShippingRequestId == requestId)
            .ToArrayAsync(ct);
        if(anchors.Length==0)anchors=await SharedShippingService.Items(db,requestId).AsNoTracking().ToArrayAsync(ct);
        DateTime since;Guid? deliveryAddress;long? sourceMessage;
        var shared=await SharedShippingService.ReadAsync(db,requestId,ct);
        NotificationOutbox? addressReference=null;
        if(anchors.Length==0)
        {
            if(shared is null||shared.ClosedAt!=null||shared.AddressReferenceId is not Guid referenceId||!shared.CustomerIds.Contains(customerId))return;
            if(!await db.Addresses.AsNoTracking().AnyAsync(a=>!a.IsDeleted&&a.Id==shared.AddressId&&a.CustomerId==shared.PrimaryCustomerId,ct))return;
            addressReference=await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==referenceId&&!n.IsDeleted&&n.Channel=="ShippingReferenceContext"&&n.CustomerId==shared.PrimaryCustomerId,ct);
            if(addressReference is null)return;
            try
            {
                using var source=System.Text.Json.JsonDocument.Parse(addressReference.Payload);var root=source.RootElement;
                if(root.GetProperty("CustomerId").GetGuid()!=shared.PrimaryCustomerId||root.GetProperty("RequestId").GetGuid()!=requestId||root.GetProperty("AddressId").GetGuid()!=shared.AddressId||
                    root.GetProperty("Kind").GetString()!="root"||!root.GetProperty("Installed").GetBoolean()||root.GetProperty("ChatId").GetString()!=addressReference.Recipient||root.GetProperty("MessageId").GetInt64()<=0)return;
                sourceMessage=root.GetProperty("MessageId").GetInt64();
            }
            catch(Exception error)when(error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return;}
            since=shared.CreatedAt;deliveryAddress=shared.AddressId;
        }
        else
        {
            since=anchors.Where(i=>i.ShippingRequestedAt!=null).Select(i=>i.ShippingRequestedAt!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
            deliveryAddress=(anchors.FirstOrDefault(i=>i.Order!.DeliveryAddressId!=null)??anchors[0]).Order!.DeliveryAddressId;
            sourceMessage=anchors.Select(i=>i.ShippingTelegramMessageId).FirstOrDefault(v=>v!=null);
        }
        var additions = await CustomerItems(db, customer).Include(i => i.Order).Where(i => i.ShippingRequestId == null &&
            i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && 
            i.Order != null && i.Order.IsInventory &&
            i.Order.Status != OrderStatus.Cancelled && i.Order.Invoices.Any(v => !v.IsDeleted && v.IssuedAt >= since && (v.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Issued || v.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Paid)))
            .ToArrayAsync(ct);
        foreach (var item in additions)
        {
            item.ShippingRequestId = requestId;
            item.ShippingRequestedAt = since;
            item.ShippingTelegramMessageId = sourceMessage;
            item.Order!.DeliveryAddressId = deliveryAddress;
            item.UpdatedAt = DateTime.UtcNow;
        }
        if(addressReference is not null&&additions.Length>0)
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=shared!.PrimaryCustomerId,Channel="SharedShippingRefresh",EventType="RefreshSharedParcelAddress",Recipient=addressReference.Recipient,
                Payload=System.Text.Json.JsonSerializer.Serialize(new{ReferenceId=addressReference.Id}),CreatedAt=DateTime.UtcNow});
    }

    public static async Task<OrderItem[]> CompleteAsync(AppDbContext db, Guid? requestId, Guid? customerId, CancellationToken ct = default)
    {
        await using var own = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
        if (requestId.HasValue)
        {
            var owner = await ValidateDispatchAsync(db, requestId.Value, customerId, ct);
            if (customerId.HasValue && customerId != owner.Id)
                throw new InvalidOperationException("بستهٔ ارسال با مشتری تطبیق ندارد؛ اقلام باید بررسی شوند.");
            customerId = owner.Id;
        }
        if (!customerId.HasValue || !requestId.HasValue) return [];
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"shared-shipping:"+requestId.Value},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Shared parcel busy',1;",ct);
        var shared=await SharedShippingService.ReadAsync(db,requestId.Value,ct);
        foreach(var owner in (shared?.CustomerIds??[customerId.Value]).Order())await LockCustomer(db,owner,ct);
        await db.OrderItems.FromSqlInterpolated($"SELECT * FROM OrderItems WITH(UPDLOCK,HOLDLOCK) WHERE ShippingRequestId={requestId.Value} AND IsDeleted=0 ORDER BY Id").AsNoTracking().ToArrayAsync(ct);
        await db.Orders.FromSqlInterpolated($"SELECT o.* FROM Orders o WITH(UPDLOCK,HOLDLOCK) WHERE EXISTS(SELECT 1 FROM OrderItems i WHERE i.OrderId=o.Id AND i.ShippingRequestId={requestId.Value} AND i.IsDeleted=0) ORDER BY o.Id").AsNoTracking().ToArrayAsync(ct);
        // Hold gift identities through the final state change; an administrator
        // editing a recipient cannot race this dispatch transaction.
        await db.SalesListRequests.FromSqlInterpolated($"SELECT r.* FROM SalesListRequests r WITH(UPDLOCK,HOLDLOCK) WHERE EXISTS(SELECT 1 FROM OrderItems i WHERE i.SourceSalesListRequestId=r.Id AND i.ShippingRequestId={requestId.Value} AND i.IsDeleted=0)").AsNoTracking().ToArrayAsync(ct);
        await ValidateDispatchAsync(db, requestId.Value, customerId, ct);
        foreach(var owner in shared?.CustomerIds??[customerId.Value])
            if ((await OpenPackages(db, owner, ct)).Contains(requestId.Value))await AttachLocked(db,owner,ct);
        await db.SaveChangesAsync(ct);
        await ValidateDispatchAsync(db, requestId.Value, customerId, ct);
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId.Value && !c.IsDeleted, ct);
        var items = await SharedShippingService.Items(db,requestId.Value).ToArrayAsync(ct);
        foreach(var item in items) await db.Entry(item).ReloadAsync(ct);
        var now = DateTime.UtcNow;
        var changed = items.Where(i => !i.IsDeleted && i.Order is { IsDeleted: false } && i.Order.Status != OrderStatus.Cancelled &&
            i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && i.ShippedAt == null).ToArray();
        foreach (var item in changed)
        {
            item.FulfillmentStatus = OrderItemFulfillmentStatus.Shipped;
            item.ShippedAt = now;
            item.UpdatedAt = now;
        }
        var changedIds = changed.Select(i => i.Id).ToArray();
        foreach (var order in changed.Select(i => i.Order!).Distinct())
        {
            if (!await db.OrderItems.AnyAsync(i => !i.IsDeleted && i.OrderId == order.Id && !changedIds.Contains(i.Id) &&
                i.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped, ct))
            {
                order.Status = OrderStatus.Shipped;
                order.ShippedAt = now;
                order.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        await ShippingPackApproval.MarkDispatchedAsync(db, customerId.Value, requestId, items.Where(i => i.FulfillmentStatus == OrderItemFulfillmentStatus.Shipped).Select(i => i.Id).ToArray(), ct);
        if(shared!=null&&(changed.Length>0||items.Length==0))
        {
            var receipt=await db.NotificationOutbox.SingleAsync(n=>n.Id==SharedShippingService.ReceiptId(requestId.Value),ct);
            receipt.Payload=System.Text.Json.JsonSerializer.Serialize(shared with{ClosedAt=shared.ClosedAt??now});receipt.UpdatedAt=now;
            await db.SaveChangesAsync(ct);
        }
        if (own != null) await own.CommitAsync(ct);
        return items;
        }
        catch
        {
            if (own != null)
            {
                await own.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }
            throw;
        }
    }
}

public sealed class ShippingRecipientValidationException(Guid requestId, IReadOnlyCollection<Guid> itemIds, string message)
    : InvalidOperationException(message)
{
    public Guid RequestId { get; } = requestId;
    public IReadOnlyCollection<Guid> ItemIds { get; } = itemIds;
}
