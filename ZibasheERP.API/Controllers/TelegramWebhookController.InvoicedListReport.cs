using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed record InvoicedListReportDraft(bool Persian, DateTime? Start, DateTime UpdatedAt);
    private static readonly ConcurrentDictionary<(long Chat, long User), InvoicedListReportDraft> InvoicedListReportDrafts = new();

    private bool CanViewInvoicedListReport(long user, long chat, string type) =>
        AdminItemActivityReport.CanView(user, chat, type, _options.OwnerUserId);

    private async Task HandleInvoicedListReportCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chat = callback.Message!.Chat;
        if (!CanViewInvoicedListReport(callback.From.Id, chat.Id, chat.Type))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این گزارش فقط برای مدیر اصلی در گفت‌وگوی خصوصی فعال است.", ct, true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var key = (chat.Id, callback.From.Id);
        if (callback.Data == "invoiceadmin:invoiced-lists:persian" || callback.Data == "invoiceadmin:invoiced-lists:gregorian")
        {
            var persian = callback.Data.EndsWith(":persian", StringComparison.Ordinal);
            foreach (var expired in InvoicedListReportDrafts.Where(entry => entry.Value.UpdatedAt < DateTime.UtcNow.AddMinutes(-15)))
                InvoicedListReportDrafts.TryRemove(expired.Key, out _);
            InvoicedListReportDrafts[key] = new(persian, null, DateTime.UtcNow);
            await SendInvoicedListDatePromptAsync(chat.Id, persian, false, ct);
            return;
        }
        InvoicedListReportDrafts.TryRemove(key, out _);
        await _sender.SendInlineKeyboardAsync(chat.Id.ToString(), "📊 تعداد لیست‌های عطر فاکتورشده\nنوع تقویم تاریخ‌های گزارش را انتخاب کنید:",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("شمسی", "invoiceadmin:invoiced-lists:persian"),
                    new TelegramInlineButton("میلادی", "invoiceadmin:invoiced-lists:gregorian") },
                new[] { new TelegramInlineButton("↩️ منوی اصلی", "invoiceadmin:menu:main") }
            }, ct);
    }

    private Task SendInvoicedListDatePromptAsync(long chat, bool persian, bool end, CancellationToken ct) =>
        ReplyAsync(chat, $"تاریخ {(end ? "پایان" : "شروع")} گزارش را به {(persian ? "شمسی" : "میلادی")} در یک پیام سه‌خطی بفرستید:\n" +
            "خط اول: روز\nخط دوم: ماه\nخط سوم: سال\n\nمثال:\n" +
            (persian ? "۱\n۷\n۱۴۰۵" : "1\n10\n2026") + "\n\nانصراف: /cancel", ct);

    private async Task<bool> TryHandleInvoicedListReportMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !InvoicedListReportDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft))
            return false;
        var key = (message.Chat.Id, message.From.Id);
        if (!CanViewInvoicedListReport(message.From.Id, message.Chat.Id, message.Chat.Type))
        {
            InvoicedListReportDrafts.TryRemove(key, out _);
            return false;
        }
        var text = message.Text?.Trim() ?? "";
        if (text.StartsWith('/'))
        {
            InvoicedListReportDrafts.TryRemove(key, out _);
            if (!text.Equals("/cancel", StringComparison.OrdinalIgnoreCase)) return false;
            await ReplyAsync(message.Chat.Id, "گزارش لغو شد.", ct);
            return true;
        }
        if (draft.UpdatedAt < DateTime.UtcNow.AddMinutes(-15))
        {
            InvoicedListReportDrafts.TryRemove(key, out _);
            await ReplyAsync(message.Chat.Id, "مهلت ورود تاریخ تمام شده؛ گزارش را دوباره از منوی مدیر انتخاب کنید.", ct);
            return true;
        }
        if (!InvoicedSalesListReport.TryParseDate(text, draft.Persian, out var date))
        {
            await ReplyAsync(message.Chat.Id, "تاریخ معتبر نیست. روز، ماه و سال را با تقویم انتخاب‌شده در سه خط وارد کنید.", ct);
            return true;
        }
        if (!draft.Start.HasValue)
        {
            InvoicedListReportDrafts[key] = draft with { Start = date, UpdatedAt = DateTime.UtcNow };
            await SendInvoicedListDatePromptAsync(message.Chat.Id, draft.Persian, true, ct);
            return true;
        }
        if (date < draft.Start.Value)
        {
            await ReplyAsync(message.Chat.Id, "تاریخ پایان نباید قبل از شروع باشد؛ تاریخ پایان را دوباره در سه خط بفرستید.", ct);
            return true;
        }
        var (since, until) = InvoicedSalesListReport.UtcRange(draft.Start.Value, date);
        var listIds = await InvoicedSalesListReport.ListIds(_db.OrderItems.AsNoTracking(), _db.Orders.AsNoTracking(), since, until).ToArrayAsync(ct);
        var count=listIds.Length;
        var costLists=await _db.SalesLists.AsNoTracking().Where(l=>!l.IsDeleted&&listIds.Contains(l.Id)).ToArrayAsync(ct);
        var costs=await new PerfumePurchaseCostService(_db).CostsAsync(costLists,ct);
        var covered=costs.Where(c=>c.CostToman.HasValue).ToArray();
        var missingCosts=count-covered.Length;
        var purchaseSummary=$"\n💱 هزینه خرید لیست‌های دارای قیمت و نرخ: {covered.Sum(c=>c.CostToman!.Value):N0} تومان\nپوشش اطلاعات خرید: {covered.Length} از {count} لیست\n"+
            (missingCosts>0?$"⚠️ {missingCosts} لیست به علت نداشتن قیمت خرید، نرخ تاریخی یا زمان تکمیل در جمع هزینه لحاظ نشده‌اند.\n":"")+
            "مبنای هزینه: قیمت خرید یک باتل × نرخ معتبر هنگام تکمیل همان لیست؛ نه نرخ روز صدور فاکتور یا گزارش. موجودی در این جمع نیست.\n";
        var inventoryCount = await InvoicedSalesListReport.InventoryIds(_db.OrderItems.AsNoTracking(), _db.Orders.AsNoTracking(), since, until).CountAsync(ct);
        InvoicedListReportDrafts.TryRemove(key, out _);
        await ReplyAsync(message.Chat.Id, "📊 گزارش تعداد لیست‌های عطر فاکتورشده\n" +
            $"از: {FormatShippingPersianDate(since)} ({draft.Start.Value:yyyy/MM/dd} میلادی)\n" +
            $"تا: {FormatShippingPersianDate(until.AddTicks(-1))} ({date:yyyy/MM/dd} میلادی)\n" +
            $"\n🧴 لیست‌های تکمیل‌شده\nتعداد لیست فاکتورشده: {count}\n\n" +
            $"📦 موجودی\nتعداد بستهٔ موجودی فاکتورشده: {inventoryCount}\n\n" + purchaseSummary+"\n"+
            "مبنا: تاریخ صدور فاکتور، به وقت ایران؛ روز پایان کامل محاسبه شده است.\n" +
            "هر لیست تکمیلی و هر بستهٔ موجودی در بخش خودش فقط یک‌بار شمرده می‌شود؛ آمار دو بخش با هم ادغام نمی‌شود. فاکتورهای حذف‌شده، لغوشده و پیش‌نویس محاسبه نمی‌شوند.", ct);
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(), "گزارش لیست‌های فاکتورشده",
            new IReadOnlyCollection<TelegramInlineButton>[] {
                new[] { new TelegramInlineButton("📅 بازه جدید", "invoiceadmin:invoiced-lists") },
                new[] { new TelegramInlineButton("↩️ منوی اصلی", "invoiceadmin:menu:main") }
            }, ct);
        return true;
    }
}
