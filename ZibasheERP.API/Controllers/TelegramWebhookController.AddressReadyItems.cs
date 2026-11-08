using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.API.Telegram;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private IQueryable<Guid> CustomerShippingItemIds(IEnumerable<Customer> customers)
    {
        var ids = _db.OrderItems.Where(i => false).Select(i => i.Id);
        foreach (var customer in customers.DistinctBy(c => c.Id))
            ids = ids.Union(ShippingRecipientPolicy.ItemsForCustomer(_db.OrderItems, _db.NotificationOutbox,
                customer.Id, customer.TelegramId, customer.Username, _db.Customers).Select(i => i.Id));
        return ids;
    }

    private IQueryable<Guid> CustomerGiftRequestIds(IEnumerable<Customer> customers)
    {
        var ids = _db.SalesListRequests.Where(r => false).Select(r => r.Id);
        foreach (var customer in customers.DistinctBy(c => c.Id))
            ids = ids.Union(ShippingRecipientPolicy.GiftRequestsForCustomer(_db.SalesListRequests, _db.Customers,
                customer.Id, customer.TelegramId, customer.Username).Select(r => r.Id));
        return ids;
    }

    private async Task<Dictionary<Guid, string>> ShippingRecipientLabelsAsync(IReadOnlyCollection<OrderItem> items, CancellationToken ct)
    {
        var manualOrderIds = items.Where(i => i.Order?.Source == OrderSource.ManualInvoice).Select(i => i.OrderId).Distinct().ToArray();
        var receipts = await _db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.OrderId != null &&
            manualOrderIds.Contains(n.OrderId.Value) && n.Channel == "Telegram" &&
            (n.EventType == "GiftInvoiceIssued" || n.EventType == "InvoiceGiftDeliveryRequiresManualAction"))
            .Select(n => new { OrderId = n.OrderId!.Value, n.CustomerId }).ToArrayAsync(ct);
        var recipientIds = receipts.Select(n => n.CustomerId).Distinct().ToArray();
        var giftIds = items.Select(i => i.SourceSalesListRequest?.GiftRecipientTelegramUserId?.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var giftNames = items.Select(i => i.SourceSalesListRequest?.GiftRecipientTelegramUsername?.Trim().TrimStart('@').ToLowerInvariant()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var recipients = await _db.Customers.AsNoTracking().Where(c => !c.IsDeleted && (recipientIds.Contains(c.Id) ||
            (c.TelegramId != null && giftIds.Contains(c.TelegramId.Trim())) ||
            (c.Username != null && giftNames.Contains(c.Username.Trim().Replace("@", "").ToLower())))).ToDictionaryAsync(c => c.Id, ct);
        var byId = recipients.Values.Where(c => !string.IsNullOrWhiteSpace(c.TelegramId)).ToLookup(c => c.TelegramId!.Trim());
        var byName = recipients.Values.Where(c => !string.IsNullOrWhiteSpace(c.Username)).ToLookup(c => c.Username!.Trim().TrimStart('@').ToLowerInvariant());
        var predicates = new Dictionary<Guid, Func<OrderItem, bool>>();
        string GiftLabel(OrderItem item)
        {
            var request = item.SourceSalesListRequest!;
            var candidates = byId[request.GiftRecipientTelegramUserId?.Trim() ?? ""].Concat(
                byName[request.GiftRecipientTelegramUsername?.Trim().TrimStart('@').ToLowerInvariant() ?? ""]).DistinctBy(c => c.Id).ToArray();
            if (candidates.Length != 1) return "⚠️ گیرندهٔ هدیه نیازمند بررسی";
            var recipient = candidates[0];
            if (!predicates.TryGetValue(recipient.Id, out var predicate))
                predicates[recipient.Id] = predicate = ShippingRecipientPolicy.ForCustomer(recipient.Id, recipient.TelegramId, recipient.Username).Compile();
            return predicate(item) ? OrderCustomerLabel(recipient) + " 🎁" : "⚠️ گیرندهٔ هدیه نیازمند بررسی";
        }
        var manualLabels = receipts.GroupBy(n => n.OrderId).ToDictionary(g => g.Key, g =>
            g.Select(n => n.CustomerId).Distinct().Count() == 1 && recipients.TryGetValue(g.First().CustomerId, out var recipient)
                ? OrderCustomerLabel(recipient) : "گیرندهٔ هدیه نیازمند بررسی");
        return items.ToDictionary(i => i.Id, i => i.SourceSalesListRequest?.IsGift == true
            ? GiftLabel(i)
            : manualLabels.TryGetValue(i.OrderId, out var label) ? label + " 🎁"
            : i.Order?.Source == OrderSource.ManualInvoice && i.Order.Notes?.Contains(ShippingRecipientPolicy.ManualGiftMarker) == true
                ? "گیرندهٔ هدیه نیازمند بررسی" : OrderCustomerLabel(i.Order?.Customer));
    }

    private IQueryable<OrderItem> CustomerShippingItems(Customer customer) =>
        ShippingRecipientPolicy.ItemsForCustomer(_db.OrderItems, _db.NotificationOutbox,
                customer.Id, customer.TelegramId, customer.Username, _db.Customers)
            .Include(item => item.Order).ThenInclude(order => order!.Customer)
            .Include(item => item.SalesList).Include(item => item.Perfume)
            .Include(item => item.Bottle).Include(item => item.SourceSalesListRequest);

    private static string FormatAddressReadyItems(IReadOnlyCollection<OrderItem> items) =>
        "📦 آیتم‌های آماده ارسال\n\n" +
        (items.Count == 0 ? "موردی ثبت نشده است." : string.Join("\n\n", items.Select((item, index) =>
            FormatShippingItemLine(item, index) +
            (item.SalesList is null ? "" : $" | کد لیست: {item.SalesList.DisplayCode}") +
            (item.SourceSalesListRequest?.IsGift == true ||
             (item.Order?.Source == OrderSource.ManualInvoice && item.Order.Notes?.Contains(ShippingRecipientPolicy.ManualGiftMarker) == true)
                ? " | 🎁 هدیه دریافتی" : "")))) +
        $"\n\n🔴 تعداد آیتم‌ها: {items.Count}";

    private async Task<TelegramSendResult> SendAddressWithReadyItemsAsync(string chatId, string addressText,
        Customer customer, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons, CancellationToken ct, Guid? requestId = null, Guid? addressId = null)
    {
        if (requestId.HasValue)
        {
            await ZibasheERP.Infrastructure.Services.GiftShippingMembershipService.RepairAsync(_db, requestId.Value, ct);
            await ZibasheERP.Infrastructure.Services.ShippingPackageService.ValidatePackageAsync(_db, requestId.Value, customer.Id, ct);
        }
        var source=requestId.HasValue?ZibasheERP.Infrastructure.Services.SharedShippingService.Items(_db,requestId.Value):CustomerShippingItems(customer);
        var items = await source.AsNoTracking()
            .Where(item => item.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && item.ShippedAt == null &&
                (!requestId.HasValue || item.ShippingRequestId == requestId))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArrayAsync(ct);
        var reference = await SaveShippingReferenceAsync(new(customer.Id, requestId, items.Select(i => i.Id).ToArray(), chatId, AddressId: addressId), ct);
        var referenceButtons = buttons.Concat(ShippingReferenceButtons(reference)).ToArray();
        var parts = SplitTelegramMessage(addressText + "\n\n" + FormatAddressReadyItems(items)).ToArray();
        var sent = await _sender.SendInlineKeyboardAsync(chatId, parts[0], referenceButtons, ct);
        if (!sent.IsSuccessful) return sent;
        await BindShippingReferenceAsync(reference, sent.MessageId, ct);
        foreach (var part in parts.Skip(1))
        {
            var continued = await _sender.SendAsync(chatId, part, ct);
            if (!continued.IsSuccessful)
                _logger.LogWarning("Address ready-items continuation failed for customer {CustomerId}: {Error}", customer.Id, continued.Error);
        }
        return sent;
    }
}
