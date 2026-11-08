using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.API.Telegram;
using System.Globalization;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private IQueryable<OrderItem> CustomerShippingItems(Customer customer) =>
        _db.OrderItems.Include(item => item.Order).ThenInclude(order => order!.Customer)
            .Include(item => item.SalesList).Include(item => item.Perfume)
            .Include(item => item.Bottle).Include(item => item.SourceSalesListRequest)
            .Where(ShippingRecipientPolicy.ForCustomer(customer.Id, customer.TelegramId, customer.Username));

    private static string FormatAddressReadyItems(IReadOnlyCollection<OrderItem> items)
    {
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
            var personalItems = groupItems.Where(item => item.SourceSalesListRequest?.IsGift != true).ToArray();
            var giftItems = groupItems.Where(item => item.SourceSalesListRequest?.IsGift == true).ToArray();
            var components = new List<string>();
            if (personalItems.Length > 0)
            {
                var identity = OrderCustomerLabel(personalItems[0].Order?.Customer);
                components.Add($"{FormatShippingVolume(personalItems.Sum(item => item.RequestedVolumeMl))} میل آیتم خودش ({identity})");
            }
            if (giftItems.Length > 0)
                components.Add($"{FormatShippingVolume(giftItems.Sum(item => item.RequestedVolumeMl))} میل هدیه دریافتی");

            var bottleDetails = groupItems
                .GroupBy(FormatShippingBottleAndLabel)
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
            return $"{index + 1}. {group.Key} — {string.Join(" + ", components)}{bottleText}{listCodeText}";
        }).ToArray();

        return "📦 آیتم‌های آماده ارسال\n\n" +
            (lines.Length == 0 ? "موردی ثبت نشده است." : string.Join("\n\n", lines)) +
            $"\n\nتعداد عطرها: {lines.Length}";
    }

    private static string FormatShippingVolume(decimal volumeMl) =>
        volumeMl.ToString("0.##", CultureInfo.InvariantCulture);

    private async Task<TelegramSendResult> SendAddressWithReadyItemsAsync(string chatId, string addressText,
        Customer customer, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons, CancellationToken ct)
    {
        var items = await CustomerShippingItems(customer).AsNoTracking()
            .Where(item => item.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArrayAsync(ct);
        var parts = SplitTelegramMessage(addressText + "\n\n" + FormatAddressReadyItems(items)).ToArray();
        var sent = await _sender.SendInlineKeyboardAsync(chatId, parts[0], buttons, ct);
        if (!sent.IsSuccessful) return sent;
        foreach (var part in parts.Skip(1))
        {
            var continued = await _sender.SendAsync(chatId, part, ct);
            if (!continued.IsSuccessful)
                _logger.LogWarning("Address ready-items continuation failed for customer {CustomerId}: {Error}", customer.Id, continued.Error);
        }
        return sent;
    }
}
