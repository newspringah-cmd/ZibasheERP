using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleGroupShippingStatusCommandAsync(TelegramMessage message, CancellationToken ct)
    {
        var command = message.Text?.Trim().Split((char[]?)null, 2,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('@', 2)[0];
        var ready = string.Equals(command, "/r", StringComparison.OrdinalIgnoreCase);
        if (!ready && !string.Equals(command, "/s", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsGroup(message.Chat.Type))
        {
            await ReplyAsync(message.Chat.Id, "این دستور را در گروه مشتری اجرا کنید.", ct);
            return true;
        }
        if (message.From is null || !IsAuthorizedShippingOperator(message.From.Id))
        {
            await ReplyAsync(message.Chat.Id, "این گزارش فقط برای مدیر و حسابدار مجاز فعال است.", ct);
            return true;
        }
        var chatId = message.Chat.Id.ToString();
        var customerIds = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(link => !link.IsDeleted && link.IsActive && !link.Customer.IsDeleted && link.ChatId == chatId)
            .Select(link => link.CustomerId).Distinct().ToArrayAsync(ct);
        if (customerIds.Length == 0)
        {
            await ReplyAsync(message.Chat.Id, "این گروه اتصال فعال به مشتری ندارد؛ ابتدا با /ad اتصال را بررسی کنید.", ct);
            return true;
        }
        var status = ready ? OrderItemFulfillmentStatus.DecantedReadyToShip : OrderItemFulfillmentStatus.Shipped;
        var items = await _db.OrderItems.AsNoTracking()
            .Where(item => !item.IsDeleted && item.Order != null && !item.Order.IsDeleted &&
                item.Order.Status != OrderStatus.Cancelled && customerIds.Contains(item.Order.CustomerId) &&
                item.FulfillmentStatus == status)
            .OrderBy(item => item.Order!.Customer!.FullName)
            .ThenByDescending(item => item.ShippedAt ?? item.UpdatedAt ?? item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                CustomerName = item.Order!.Customer!.FullName,
                Name = item.SalesList != null ? item.SalesList.PersianName :
                    item.Perfume != null ? item.Perfume.Name : item.ManualDescription,
                Code = item.SalesList != null ? (int?)(item.SalesList.StablePublicCode ?? item.SalesList.PublicCode) : null,
                item.RequestedVolumeMl,
                item.IsBottleOwner,
                item.ShippedAt
            }).ToArrayAsync(ct);
        var title = ready ? "📦 آیتم‌های دکانت‌شده و آماده ارسال" : "🚚 آیتم‌های ارسال‌شده قبلی";
        var lines = items.Select((item, index) =>
            $"{index + 1}. {item.Name ?? "آیتم سفارش"}{FormatListCode(item.Code)} — {item.RequestedVolumeMl} میل" +
            (item.IsBottleOwner ? " | صاحب باتل" : string.Empty) +
            (customerIds.Length > 1 ? $"\nمشتری: {item.CustomerName}" : string.Empty) +
            (!ready && item.ShippedAt.HasValue
                ? $"\nتاریخ ارسال: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(item.ShippedAt.Value, DateTimeKind.Utc), "Asia/Tehran"):yyyy/MM/dd}"
                : string.Empty));
        var report = title + "\n\n" + (items.Length == 0 ? "موردی ثبت نشده است." : string.Join("\n\n", lines)) +
            $"\n\nتعداد آیتم‌ها: {items.Length}";
        foreach (var part in SplitTelegramMessage(report))
            await ReplyAsync(message.Chat.Id, part, ct);
        return true;
    }
}
