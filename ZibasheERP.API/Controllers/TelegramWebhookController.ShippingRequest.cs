using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleShippingRequestCallbackAsync(
        TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            !callback.Data.StartsWith("shipping:", StringComparison.Ordinal))
            return false;

        if (callback.Data == "shipping:request")
        {
            if (!string.Equals(callback.Message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "درخواست پست فقط در گفت‌وگوی خصوصی ربات ثبت می‌شود.", ct, true);
                return true;
            }
            await CreateCustomerShippingRequestAsync(callback, ct);
            return true;
        }

        if (!callback.Data.StartsWith("shipping:sent:", StringComparison.Ordinal) ||
            !Guid.TryParseExact(callback.Data["shipping:sent:".Length..], "N", out var requestId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال نامعتبر است.", ct);
            return true;
        }
        if (!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
            return true;
        }

        var items = await _db.OrderItems.Include(value => value.Order)
            .Where(value => !value.IsDeleted && value.ShippingRequestId == requestId).ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "اقلام این درخواست پیدا نشد.", ct, true);
            return true;
        }
        if (items.All(value => value.FulfillmentStatus == OrderItemFulfillmentStatus.Shipped))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این درخواست قبلاً ارسال‌شده ثبت شده است ✅", ct, true);
            return true;
        }
        if (items.Any(value => value.FulfillmentStatus != OrderItemFulfillmentStatus.DecantedReadyToShip))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "وضعیت بعضی اقلام تغییر کرده؛ عملیات متوقف شد.", ct, true);
            return true;
        }
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.FulfillmentStatus = OrderItemFulfillmentStatus.Shipped;
            item.ShippedAt = now;
            item.UpdatedAt = now;
        }
        foreach (var order in items.Select(value => value.Order).Where(value => value is not null).Distinct()!)
        {
            var hasUnshipped = await _db.OrderItems.AnyAsync(value => !value.IsDeleted && value.OrderId == order!.Id &&
                value.ShippingRequestId != requestId && value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped, ct);
            if (!hasUnshipped)
            {
                order!.Status = OrderStatus.Shipped;
                order.ShippedAt = now;
                order.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        await _sender.AnswerCallbackAsync(callback.Id, $"{items.Length} آیتم ارسال‌شده ثبت شد ✅", ct, true);
        await ReplyAsync(callback.Message.Chat.Id, $"✅ درخواست ارسال با {items.Length} آیتم تکمیل شد.", ct);
        return true;
    }

    private async Task CreateCustomerShippingRequestAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var telegramId = callback.From.Id.ToString();
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.TelegramId == telegramId, ct);
        if (customer is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "حساب مشتری متصل نیست.", ct, true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "گروه مسئول ارسال هنوز تنظیم نشده است.", ct, true);
            return;
        }
        var existing = await _db.OrderItems.AsNoTracking().FirstOrDefaultAsync(value => !value.IsDeleted &&
            value.Order != null && value.Order.CustomerId == customer.Id &&
            value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip &&
            value.ShippingRequestId != null, ct);
        if (existing is not null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال این اقلام قبلاً ثبت شده و در انتظار مسئول ارسال است.", ct, true);
            return;
        }
        var items = await _db.OrderItems
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList)
            .Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.CustomerId == customer.Id &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip &&
                value.ShippingRequestId == null).OrderBy(value => value.CreatedAt).ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "آیتم آماده ارسالی پیدا نشد.", ct, true);
            return;
        }
        var address = await _db.Addresses.AsNoTracking().Where(value => !value.IsDeleted && value.CustomerId == customer.Id)
            .OrderByDescending(value => value.IsDefault).ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (address is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ابتدا آدرس ارسال را در بخش آدرس‌های من ثبت کنید.", ct, true);
            return;
        }
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.ShippingRequestId = requestId;
            item.ShippingRequestedAt = now;
            item.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        var lines = items.Select((item, index) =>
            $"{index + 1}. {item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر"} — {item.RequestedVolumeMl} میل");
        var message = $"📦 درخواست ارسال جدید\n\nمشتری: {OrderCustomerLabel(customer)}\n" +
            $"گیرنده: {address.ReceiverName}\nموبایل: {address.Mobile}\nکدپستی: {address.PostalCode}\n" +
            $"آدرس: {address.Province}، {address.City}، {address.FullAddress}\n\nاقلام آماده ارسال:\n{string.Join("\n", lines)}";
        var sent = await _sender.SendInlineKeyboardAsync(_options.ShippingChatId.Trim(), message,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("🚚 ارسال شد", $"shipping:sent:{requestId:N}") }
            }, ct);
        if (!sent.IsSuccessful)
        {
            foreach (var item in items)
            {
                item.ShippingRequestId = null;
                item.ShippingRequestedAt = null;
                item.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, $"ارسال درخواست به مسئول ارسال ناموفق بود: {sent.Error}", ct, true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال ثبت شد ✅", ct, true);
        await ReplyAsync(callback.Message!.Chat.Id,
            $"درخواست ارسال {items.Length} آیتم آماده برای مسئول ارسال ثبت شد ✅", ct);
    }

    private async Task<bool> IsAuthorizedShippingAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        if (IsPrimaryOwner(userId)) return true;
        if (!string.Equals(chatId.ToString(), _options.ShippingChatId.Trim(), StringComparison.Ordinal))
            return false;
        return await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct);
    }
}
