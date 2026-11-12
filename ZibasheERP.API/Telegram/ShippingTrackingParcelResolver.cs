using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;

// Reposted addresses may have a customer-only button while their frozen item
// snapshot still belongs to an older, explicitly registered parcel. Resolve from
// that exact source message, never from the customer's newest/sole open parcel.
public static class ShippingTrackingParcelResolver
{
    private sealed record Root(Guid CustomerId, Guid? RequestId, Guid[] ItemIds, string ChatId,
        string Kind, long? MessageId, bool Installed, Guid? AddressId);

    public static async Task<Guid?> ResolveAsync(AppDbContext db, Guid customerId, string chatId,
        long messageId, Guid? expectedRequest = null, CancellationToken ct = default)
    {
        var stored = await db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted &&
            n.Channel == "ShippingReferenceContext" && n.EventType == "AddressReferences" &&
            n.CustomerId == customerId && n.Recipient == chatId).Select(n => new { n.Payload, n.CreatedAt }).ToArrayAsync(ct);
        var roots = stored.Select(n => (Root: Parse(n.Payload), n.CreatedAt))
            .Where(n => n.Root is { Kind: "root", Installed: true } && n.Root.CustomerId == customerId && n.Root.ChatId == chatId).ToArray();
        var source = roots.Where(n => n.Root!.MessageId == messageId).ToArray();
        if (source.Length == 0)
        {
            // Preserve legacy explicit-request buttons and address-only tracking.
            if (expectedRequest.HasValue) await ShippingPackageService.ValidateDispatchAsync(db, expectedRequest.Value, customerId, ct);
            return expectedRequest;
        }
        if (source.Length != 1) throw Review();
        var root = source[0].Root!;
        if (root.RequestId is Guid explicitRequest)
        {
            if (expectedRequest.HasValue && expectedRequest != explicitRequest) throw Review();
            await ShippingPackageService.ValidateDispatchAsync(db, explicitRequest, customerId, ct);
            return explicitRequest;
        }
        if (root.ItemIds.Length == 0)
        {
            if (expectedRequest.HasValue) throw Review();
            return null; // Old-system parcels with no ERP items may still receive tracking.
        }
        if (root.AddressId is null || root.ItemIds.Distinct().Count() != root.ItemIds.Length) throw Review();
        var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        var items = await ShippingPackageService.CustomerItems(db, customer).AsNoTracking()
            .Where(i => root.ItemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.ShippingRequestId, i.ShippingRequestedAt, i.ShippingTelegramMessageId }).ToArrayAsync(ct);
        var requests = items.Select(i => i.ShippingRequestId).Distinct().ToArray();
        // Explicit manual registrations may describe old-system stock with no
        // registered parcel. Keep owner delivery working without shipping any
        // unassigned ERP items by inference.
        if (!expectedRequest.HasValue && items.Length == root.ItemIds.Length && requests.Length == 1 && requests[0] is null &&
            customer.Notes?.StartsWith("manual-address-registration:", StringComparison.Ordinal) == true) return null;
        if (items.Length != root.ItemIds.Length || requests.Length != 1 || requests[0] is not Guid request ||
            expectedRequest.HasValue && expectedRequest != request ||
            items.Any(i => i.ShippingRequestedAt is null || i.ShippingRequestedAt > source[0].CreatedAt)) throw Review();
        // The old parcel's original address must agree with the repost. An item
        // moved into a later/different parcel cannot lend identity to an old button.
        if (!roots.Any(n => n.Root!.RequestId == request && n.Root.AddressId == root.AddressId &&
            n.CreatedAt <= source[0].CreatedAt && n.Root.MessageId is > 0 &&
            items.All(i => i.ShippingTelegramMessageId == n.Root.MessageId) &&
            root.ItemIds.All(id => n.Root.ItemIds.Contains(id)))) throw Review();
        await ShippingPackageService.ValidateDispatchAsync(db, request, customerId, ct);
        return request;
    }

    private static Root? Parse(string payload)
    {
        try { return JsonSerializer.Deserialize<Root>(payload); }
        catch (JsonException) { return null; }
    }
    private static InvalidOperationException Review() => new(
        "اقلام این آدرس با یک بستهٔ ثبت‌شده تطبیق قطعی ندارد؛ اتصال بسته باید بررسی شود. کد رهگیری ارسال نشد.");
}
