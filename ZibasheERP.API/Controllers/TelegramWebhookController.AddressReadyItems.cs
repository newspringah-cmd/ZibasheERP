using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.API.Telegram;
using System.Globalization;

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

    private static string FormatAddressReadyItems(
        IReadOnlyCollection<OrderItem> items,
        IReadOnlyDictionary<(Guid SalesListId, decimal VolumeMl), IReadOnlyCollection<string>>? selectedFancyModels = null)
    {
        selectedFancyModels ??= new Dictionary<(Guid SalesListId, decimal VolumeMl), IReadOnlyCollection<string>>();
        static bool Gift(OrderItem item) => item.SourceSalesListRequest?.IsGift == true ||
            item.Order?.Source == OrderSource.ManualInvoice && item.Order.Notes?.Contains(ShippingRecipientPolicy.ManualGiftMarker) == true;
        var perfumeGroups = items
            .GroupBy(item =>
            {
                var perfumeId = item.PerfumeId ?? item.SalesList?.PerfumeId;
                return perfumeId.HasValue
                    ? $"perfume:{perfumeId.Value:N}"
                    : $"name:{ShippingItemName(item).Trim()}";
            }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.CreatedAt))
            .ToArray();
        var lines = perfumeGroups.Select((group, index) =>
        {
            var groupItems = group.ToArray();
            var personalItems = groupItems.Where(item => !Gift(item)).ToArray();
            var giftItems = groupItems.Where(Gift).ToArray();
            var components = new List<string>();
            if (personalItems.Length > 0)
            {
                var identity = OrderCustomerLabel(personalItems[0].Order?.Customer);
                components.Add($"{FormatShippingVolume(personalItems.Sum(item => item.RequestedVolumeMl))} میل آیتم خودش ({identity})");
            }
            if (giftItems.Length > 0)
                components.Add($"{FormatShippingVolume(giftItems.Sum(item => item.RequestedVolumeMl))} میل هدیه دریافتی");

            var bottleDetails = groupItems
                .GroupBy(item => FormatShippingBottleAndLabel(item, GetPreviouslySelectedFancyModels(item, selectedFancyModels)))
                .Select(bottleGroup =>
                    $"{FormatShippingVolume(bottleGroup.Sum(item => item.RequestedVolumeMl))} میل {bottleGroup.Key}")
                .ToArray();
            var listCodes = groupItems.Where(item => item.SalesList is not null)
                .Select(item => item.SalesList!.DisplayCode).Distinct().ToArray();
            var listCodeText = listCodes.Length switch
            {
                0 => string.Empty,
                1 => $" | کد لیست: {listCodes[0]}",
                _ => $" | کد لیست‌ها: {string.Join("، ", listCodes)}"
            };
            var bottleText = bottleDetails.Length == 0
                ? string.Empty
                : $" | جزئیات شیشه: {string.Join("؛ ", bottleDetails)}";
            return $"{index + 1}. {ShippingItemName(groupItems[0]).Trim()} — {string.Join(" + ", components)}{bottleText}{listCodeText}";
        }).ToArray();

        return "📦 آیتم‌های آماده ارسال\n\n" +
            (lines.Length == 0 ? "موردی ثبت نشده است." : string.Join("\n\n", lines)) +
            $"\n\nتعداد عطرها: {lines.Length}\n🔴 تعداد آیتم‌ها: {items.Count}";
    }

    private async Task<IReadOnlyDictionary<(Guid SalesListId, decimal VolumeMl), IReadOnlyCollection<string>>>
        GetSelectedFancyModelsAsync(IReadOnlyCollection<OrderItem> items, CancellationToken ct)
    {
        var salesListIds = items
            .Where(item => item.Bottle?.Type == BottleType.Fancy)
            .Select(item => item.SalesListId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (salesListIds.Length == 0)
            return new Dictionary<(Guid SalesListId, decimal VolumeMl), IReadOnlyCollection<string>>();

        var requests = await _db.SalesListRequests.AsNoTracking()
            .Where(request => !request.IsDeleted && request.Kind == SalesListRequestKind.CurrentBottle &&
                (request.Status == SalesListRequestStatus.Confirmed ||
                 request.Status == SalesListRequestStatus.Promoted ||
                 request.Status == SalesListRequestStatus.QueuedForInvoice ||
                 request.Status == SalesListRequestStatus.Invoiced) &&
                request.Bottle != null && request.Bottle.Type == BottleType.Fancy &&
                salesListIds.Contains(request.SalesListId))
            .Select(request => new
            {
                request.SalesListId,
                request.VolumeMl,
                request.AdminNotes,
                BottleName = request.Bottle!.Name
            })
            .ToArrayAsync(ct);

        return requests
            .Select(request => new
            {
                request.SalesListId,
                request.VolumeMl,
                Model = GetSelectedFancyModel(request.AdminNotes, request.BottleName)
            })
            .Where(request => !string.IsNullOrWhiteSpace(request.Model))
            .GroupBy(request => (request.SalesListId, request.VolumeMl))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyCollection<string>)group.Select(request => request.Model!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyCollection<string> GetPreviouslySelectedFancyModels(
        OrderItem item,
        IReadOnlyDictionary<(Guid SalesListId, decimal VolumeMl), IReadOnlyCollection<string>> selectedFancyModels)
    {
        if (item.Bottle?.Type != BottleType.Fancy || !item.SalesListId.HasValue ||
            GetSelectedFancyModel(item.SourceSalesListRequest?.AdminNotes, item.Bottle.Name) is not null)
            return [];

        return selectedFancyModels.TryGetValue((item.SalesListId.Value, item.RequestedVolumeMl), out var selected)
            ? selected
            : [];
    }

    private static string? GetSelectedFancyModel(string? adminNotes, string? bottleName)
    {
        if (!string.IsNullOrWhiteSpace(adminNotes)) return adminNotes.Trim();
        if (string.IsNullOrWhiteSpace(bottleName)) return null;
        string[] knownModels = ["سر آبی", "سر قرمز", "سر سیاه", "سر مشکی", "سر طلایی", "سر نقره‌ای", "سر نقره اي", "آبی", "قرمز", "سیاه", "مشکی", "طلایی", "نقره‌ای", "نقره اي", "رزگلد", "blue", "red", "black", "gold", "silver", "rose gold"];
        return knownModels.FirstOrDefault(model => bottleName.Contains(model, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatFancyModelAvailability(IReadOnlyCollection<string> alreadySelectedModels) =>
        alreadySelectedModels.Count == 0
            ? "مدل تعیین نشده؛ همه مدل‌ها مجازند"
            : $"مدل تعیین نشده؛ همه مدل‌ها مجاز به‌جز {string.Join(" و ", alreadySelectedModels)}";

    private static string FormatShippingVolume(decimal volumeMl) =>
        volumeMl.ToString("0.##", CultureInfo.InvariantCulture);

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
        var context=new ShippingReferenceContext(customer.Id,requestId,items.Select(i=>i.Id).ToArray(),chatId,AddressId:addressId);
        var reference = await SaveShippingReferenceAsync(context, ct);
        var previous=requestId.HasValue?await FindShippingPackAppendBaseAsync(context,items,ct):null;
        if(requestId is null && items.Length==0 && addressId.HasValue &&
            ZibasheERP.Infrastructure.Services.ManualShippingRegistration.TrackingDestination(customer,_options.OwnerUserId) is not null)
            buttons=new[]{(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton("✅ ارسال شد",ManualSentPrefix+reference.ToString("N"))}}.Concat(buttons).ToArray();
        var referenceButtons = buttons.Concat(previous is null?ShippingReferenceButtons(reference):ShippingReferenceButtonsWithPhotoCount(reference,previous.Data.PhotoFileIds.Length)).ToArray();
        var selectedFancyModels = await GetSelectedFancyModelsAsync(items, ct);
        var parts = SplitTelegramMessage(addressText + "\n\n" + FormatAddressReadyItems(items, selectedFancyModels)).ToArray();
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
