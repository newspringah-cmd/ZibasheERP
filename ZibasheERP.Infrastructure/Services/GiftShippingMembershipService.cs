using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

// Repairs historical parcel links only. Financial ownership remains with the
// donor; current recipient policy continues to govern reports and dispatch.
public static class GiftShippingMembershipService
{
    public sealed record Detachment(Guid ItemId, Guid RecipientId);
    public sealed record Review(Guid RequestId, Guid? PrimaryId, Detachment[] Items, string? Blocker);
    public const string AuditChannel = "GiftParcelMembershipRepair";

    public static async Task<Review> InspectAsync(AppDbContext db, Guid request, CancellationToken ct = default)
    {
        var items = await SharedShippingService.Items(db, request).AsNoTracking().ToArrayAsync(ct);
        if (items.Length == 0) return new(request, null, [], "empty");
        if (items.Any(i => i.ShippedAt != null || i.FulfillmentStatus == OrderItemFulfillmentStatus.Shipped))
            return new(request, null, [], "dispatched");
        if (await db.TrackingDispatches.AnyAsync(d => !d.IsDeleted && d.ShippingRequestId == request &&
            (d.Status == TrackingDispatchStatus.Ready || d.Status == TrackingDispatchStatus.Sending || d.Status == TrackingDispatchStatus.Sent ||
             d.Deliveries.Any(p => p.AttemptedAt != null || p.SentAt != null)), ct))
            return new(request, null, [], "tracking-active");
        Customer primary;
        try { primary = await ShippingPackageService.PackageCustomerAsync(db, request, ct); }
        catch (InvalidOperationException) { return new(request, null, [], "ambiguous-primary"); }
        var shared = await SharedShippingService.ReadAsync(db, request, ct);
        if (shared?.ClosedAt != null) return new(request, primary.Id, [], "closed-shared-parcel");
        var memberIds = shared?.CustomerIds ?? [primary.Id];
        var members = await db.Customers.AsNoTracking().Where(c => memberIds.Contains(c.Id) && !c.IsDeleted).ToArrayAsync(ct);
        if (members.Length != memberIds.Length) return new(request, primary.Id, [], "missing-member");
        var owned = new HashSet<Guid>();
        foreach (var member in members)
            owned.UnionWith(await ShippingPackageService.CustomerItems(db, member).AsNoTracking()
                .Where(i => i.ShippingRequestId == request).Select(i => i.Id).ToArrayAsync(ct));
        var invalid = items.Where(i => !owned.Contains(i.Id) && i.Order is { IsDeleted: false } &&
            i.Order.Status != OrderStatus.Cancelled && memberIds.Contains(i.Order.CustomerId) &&
            i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip).ToArray();
        var result = new List<Detachment>();
        foreach (var item in invalid)
        {
            var source = item.SourceSalesListRequest;
            var receipts = await db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.OrderId == item.OrderId &&
                n.Channel == "Telegram" && (n.EventType == "GiftInvoiceIssued" || n.EventType == "InvoiceGiftDeliveryRequiresManualAction"))
                .Select(n => n.CustomerId).Distinct().ToArrayAsync(ct);
            var ordinaryGift = source is { IsDeleted: false, IsGift: true };
            var manualGift = item.Order!.Source == OrderSource.ManualInvoice && receipts.Length == 1;
            if (!ordinaryGift && !manualGift) continue;
            var numeric = source?.GiftRecipientTelegramUserId?.Trim() ?? "";
            var name = source?.GiftRecipientTelegramUsername?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
            var candidates = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted &&
                (receipts.Contains(c.Id) || numeric != "" && c.TelegramId != null && c.TelegramId.Trim() == numeric ||
                 name != "" && c.Username != null && c.Username.Trim().Replace("@", "").ToLower() == name)).ToArrayAsync(ct);
            var matches = new List<Guid>();
            foreach (var candidate in candidates)
                if (await ShippingPackageService.CustomerItems(db, candidate).AsNoTracking().AnyAsync(i => i.Id == item.Id, ct))
                    matches.Add(candidate.Id);
            if (matches.Count != 1 || memberIds.Contains(matches[0])) continue;
            // Conflicting durable gift delivery evidence remains a manual case.
            if (receipts.Any(id => id != matches[0])) continue;
            result.Add(new(item.Id, matches[0]));
        }
        return new(request, primary.Id, result.ToArray(), null);
    }

    public static async Task<int> RepairAsync(AppDbContext db, Guid request, CancellationToken ct = default)
    {
        var initial = await InspectAsync(db, request, ct);
        if (initial.Items.Length == 0 || initial.PrimaryId == null) return 0;
        await using var own = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"shared-shipping:" + request},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Parcel busy',1;", ct);
        await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={initial.PrimaryId.Value}").AsNoTracking().SingleAsync(ct);
        await db.OrderItems.FromSqlInterpolated($"SELECT * FROM OrderItems WITH(UPDLOCK,HOLDLOCK) WHERE ShippingRequestId={request} AND IsDeleted=0 ORDER BY Id").AsNoTracking().ToArrayAsync(ct);
        await db.Orders.FromSqlInterpolated($"SELECT o.* FROM Orders o WITH(UPDLOCK,HOLDLOCK) WHERE EXISTS(SELECT 1 FROM OrderItems i WHERE i.OrderId=o.Id AND i.ShippingRequestId={request} AND i.IsDeleted=0) ORDER BY o.Id").AsNoTracking().ToArrayAsync(ct);
        await db.SalesListRequests.FromSqlInterpolated($"SELECT r.* FROM SalesListRequests r WITH(UPDLOCK,HOLDLOCK) WHERE EXISTS(SELECT 1 FROM OrderItems i WHERE i.SourceSalesListRequestId=r.Id AND i.ShippingRequestId={request} AND i.IsDeleted=0)").AsNoTracking().ToArrayAsync(ct);
        // Tracking send claims take the same dispatch row lock. Never alter a
        // parcel whose code was queued, attempted, sent or partly dispatched.
        await db.TrackingDispatches.FromSqlInterpolated($"SELECT * FROM TrackingDispatches WITH(UPDLOCK,HOLDLOCK) WHERE ShippingRequestId={request} AND IsDeleted=0").AsNoTracking().ToArrayAsync(ct);
        var current = await InspectAsync(db, request, ct);
        if (current.PrimaryId != initial.PrimaryId || current.Items.Length == 0)
        { if (own != null) await own.CommitAsync(ct); return 0; }
        var ids = current.Items.Select(i => i.ItemId).ToArray();
        var now = DateTime.UtcNow;
        var changed = await db.OrderItems.Where(i => ids.Contains(i.Id) && !i.IsDeleted && i.ShippingRequestId == request &&
            i.ShippedAt == null && i.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ShippingRequestId, (Guid?)null)
                .SetProperty(i => i.ShippingRequestedAt, (DateTime?)null).SetProperty(i => i.ShippingTelegramMessageId, (long?)null)
                .SetProperty(i => i.UpdatedAt, now), ct);
        if (changed != ids.Length) throw new InvalidOperationException("عضویت هدیه‌ها تغییر کرد؛ اصلاح بسته متوقف شد.");
        foreach (var entry in db.ChangeTracker.Entries<OrderItem>().Where(e => ids.Contains(e.Entity.Id)))
        {
            foreach (var property in new[] { nameof(OrderItem.ShippingRequestId), nameof(OrderItem.ShippingRequestedAt), nameof(OrderItem.ShippingTelegramMessageId) })
            { entry.Property(property).CurrentValue = null; entry.Property(property).OriginalValue = null; }
            entry.Property(nameof(OrderItem.UpdatedAt)).CurrentValue = now;
            entry.Property(nameof(OrderItem.UpdatedAt)).OriginalValue = now;
        }
        db.NotificationOutbox.Add(new NotificationOutbox { Id = Guid.NewGuid(), CustomerId = current.PrimaryId!.Value,
            Channel = AuditChannel, EventType = "OutgoingGiftParcelLinksRemoved", Recipient = request.ToString(),
            Payload = JsonSerializer.Serialize(new { RequestId = request, PrimaryId = current.PrimaryId, Items = current.Items }),
            Status = NotificationOutboxStatus.Processed, CreatedAt = now, ProcessedAt = now });
        await db.SaveChangesAsync(ct);
        if (own != null) await own.CommitAsync(ct);
        return changed;
    }

    public static async Task RepairCustomerAsync(AppDbContext db, Guid customerId, CancellationToken ct = default)
    {
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        var paidRequests = db.OrderItems.AsNoTracking().Where(i => !i.IsDeleted && i.ShippingRequestId != null && i.ShippedAt == null &&
            i.Order != null && !i.Order.IsDeleted && i.Order.CustomerId == customerId)
            .Select(i => i.ShippingRequestId!.Value);
        var receivedRequests = ShippingPackageService.CustomerItems(db, customer).AsNoTracking()
            .Where(i => i.ShippingRequestId != null && i.ShippedAt == null).Select(i => i.ShippingRequestId!.Value);
        var requests = await paidRequests.Union(receivedRequests).ToArrayAsync(ct);
        foreach (var request in requests) await RepairAsync(db, request, ct);
    }
}
