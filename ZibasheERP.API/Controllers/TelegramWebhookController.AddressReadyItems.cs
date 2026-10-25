using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.API.Telegram;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private IQueryable<OrderItem> CustomerShippingItems(Customer customer) =>
        _db.OrderItems.Include(item => item.Order).ThenInclude(order => order!.Customer)
            .Include(item => item.SalesList).Include(item => item.Perfume)
            .Include(item => item.Bottle).Include(item => item.SourceSalesListRequest)
            .Where(ShippingRecipientPolicy.ForCustomer(customer.Id, customer.TelegramId, customer.Username));

    private static string FormatAddressReadyItems(IReadOnlyCollection<OrderItem> items) =>
        "📦 آیتم‌های آماده ارسال\n\n" +
        (items.Count == 0 ? "موردی ثبت نشده است." : string.Join("\n\n", items.Select((item, index) =>
            FormatShippingItemLine(item, index) +
            (item.SalesList is null ? "" : $" | کد لیست: {item.SalesList.DisplayCode}") +
            (item.SourceSalesListRequest?.IsGift == true ? " | 🎁 هدیه دریافتی" : "")))) +
        $"\n\nتعداد آیتم‌ها: {items.Count}";

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
