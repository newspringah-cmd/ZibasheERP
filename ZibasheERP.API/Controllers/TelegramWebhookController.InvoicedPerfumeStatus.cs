using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string InvoicedStatusReport = "invoiceadmin:invoiced-lists:status";
    private const string InvoicedStatusChoice = "invoiceadmin:invoiced-lists:s:";

    private IQueryable<OrderItem> InvoicedPerfumeItems() => _db.OrderItems.AsNoTracking().Where(item =>
        !item.IsDeleted && item.Order != null && !item.Order.IsDeleted &&
        item.Order.Source == OrderSource.SalesListInvoice && !item.Order.IsInventory &&
        item.Order.Status != OrderStatus.Cancelled &&
        item.Order.Invoices.Any(invoice => !invoice.IsDeleted &&
            (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.Paid)));

    private IQueryable<SalesList> InvoicedPerfumeCycles()
    {
        var listIds = InvoicedPerfumeItems().Select(item => item.SalesListId ?? item.Order!.SalesListId);
        return _db.SalesLists.AsNoTracking().Where(list => !list.IsInventoryOffer && listIds.Contains(list.Id));
    }

    private IQueryable<Perfume> InvoicedPerfumeSearch(string text)
    {
        var term = NormalizeNumber(text).Replace("ي", "ی").Replace("ك", "ک").Replace("\u200c", "").ToLowerInvariant();
        var cycles = InvoicedPerfumeCycles();
        if (int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            cycles = cycles.Where(list => list.PublicCode == code || list.StablePublicCode == code);
        }
        else
        {
            cycles = cycles.Where(list =>
                list.PersianName.ToLower().Replace("ي", "ی").Replace("ك", "ک").Replace(" ", "").Replace("\u200c", "").Contains(term) ||
                list.Perfume.Name.ToLower().Replace("ي", "ی").Replace("ك", "ک").Replace(" ", "").Replace("\u200c", "").Contains(term) ||
                list.EnglishName.ToLower().Replace(" ", "").Contains(term) ||
                list.Perfume.EnglishName.ToLower().Replace(" ", "").Contains(term));
        }
        var perfumeIds = cycles.Select(list => list.PerfumeId);
        return _db.Perfumes.AsNoTracking().Where(perfume => perfumeIds.Contains(perfume.Id)).OrderBy(perfume => perfume.Name);
    }

    private async Task SearchInvoicedPerfumeStatusAsync((long Chat, long User) key, string text,
        InvoicedListReportDraft draft, CancellationToken ct)
    {
        if (NormalizeNumber(text).Replace("\u200c", "").Length < 2 || text.Length > 200)
        {
            await ReplyAsync(key.Chat, "نام عطر یا کد معتبر لیست را بفرستید.", ct);
            return;
        }

        const int maximumChoices = 20;
        var matches = await InvoicedPerfumeSearch(text).Take(maximumChoices + 1).ToArrayAsync(ct);
        if (matches.Length == 0 || matches.Length > maximumChoices)
        {
            InvoicedListReportDrafts[key] = draft with { PerfumeChoices = null, UpdatedAt = DateTime.UtcNow };
            await ReplyAsync(key.Chat, matches.Length == 0
                ? "لیست فاکتورشده‌ای پیدا نشد. نام دیگری یا کد لیست را وارد کنید."
                : "چندین عطر پیدا شد. نام دقیق‌تر یا کد لیست را وارد کنید.", ct);
            return;
        }
        if (matches.Length == 1)
        {
            InvoicedListReportDrafts.TryRemove(key, out _);
            await SendInvoicedPerfumeStatusAsync(key.Chat, matches[0].Id, ct);
            return;
        }

        InvoicedListReportDrafts[key] = draft with
        {
            PerfumeChoices = matches.Select(perfume => perfume.Id).ToArray(), UpdatedAt = DateTime.UtcNow
        };
        var buttons = matches.Select(perfume => (IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton(Limit(InvoicedPerfumeName(perfume), 55), InvoicedStatusChoice + perfume.Id.ToString("N"))
        }).ToArray();
        await _sender.SendInlineKeyboardAsync(key.Chat.ToString(), "عطر موردنظر را انتخاب کنید یا کد لیست را بفرستید.", buttons, ct);
    }

    private async Task SendInvoicedPerfumeStatusAsync(long chat, Guid perfumeId, CancellationToken ct)
    {
        var cycles = await InvoicedPerfumeCycles().Where(list => list.PerfumeId == perfumeId)
            .Include(list => list.Perfume).OrderByDescending(list => list.OpenDate).ThenBy(list => list.Id).ToArrayAsync(ct);
        var ids = cycles.Select(list => (Guid?)list.Id).ToArray();
        var items = await InvoicedPerfumeItems().Where(item => ids.Contains(item.SalesListId ?? item.Order!.SalesListId))
            .Include(item => item.Order).ThenInclude(order => order!.Customer)
            .Include(item => item.SourceSalesListRequest)
            .OrderBy(item => item.RowNumber).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await ReplyAsync(chat, "آیتم فاکتورشده‌ای برای این عطر پیدا نشد.", ct);
            return;
        }

        var recipients = await ShippingRecipientLabelsAsync(items, ct);
        var byList = items.ToLookup(item => item.SalesListId ?? item.Order!.SalesListId);
        var activeCycles = cycles.Where(list => byList.Contains(list.Id)).ToArray();
        var title = InvoicedPerfumeName(activeCycles[0].Perfume);
        var report = string.Join("\n\n", activeCycles.Select((list, index) =>
            FormatInvoicedPerfumeCycle(list, index + 1, activeCycles.Length, byList[list.Id], recipients)));
        foreach (var part in SplitTelegramMessage($"📋 {title}\n\n{report}"))
            await ReplyAsync(chat, part, ct);

        var summary = $"📊 جمع وضعیت {title}\nتعداد لیست‌ها: {activeCycles.Length}\nتعداد کل اقلام: {items.Sum(item => item.Quantity)}\n\n" +
            FormatInvoicedPerfumeTotals(items);
        var summaryParts = SplitTelegramMessage(summary).ToArray();
        foreach (var part in summaryParts.SkipLast(1))
            await ReplyAsync(chat, part, ct);
        await _sender.SendInlineKeyboardAsync(chat.ToString(), summaryParts[^1],
            [[new TelegramInlineButton("🔎 جستجوی عطر دیگر", InvoicedStatusReport)],
             [new TelegramInlineButton("↩️ فاکتورها", "invoiceadmin:menu:invoices")]], ct);
    }

    private static string InvoicedPerfumeName(Perfume perfume) =>
        string.IsNullOrWhiteSpace(perfume.EnglishName) || perfume.Name == perfume.EnglishName
            ? perfume.Name : $"{perfume.Name} | {perfume.EnglishName}";

    private static string FormatInvoicedPerfumeCycle(SalesList list, int number, int count,
        IEnumerable<OrderItem> items, IReadOnlyDictionary<Guid, string> recipients) =>
        $"🧴 لیست {list.DisplayCode} — نوبت {number} از {count}\nتاریخ باز شدن: {FormatShippingPersianDate(list.OpenDate)}\n" +
        string.Join("\n", items.Select((item, index) =>
            $"{index + 1}. {recipients[item.Id]} — {FormatShippingVolume(item.RequestedVolumeMl)} میل" +
            (item.Quantity > 1 ? $" × {item.Quantity}" : string.Empty) +
            (item.IsBottleOwner ? " 👑 باتل اصلی" : string.Empty) +
            $" — {OrderItemFulfillmentStatusLabel(item.FulfillmentStatus)}"));

    private static string FormatInvoicedPerfumeTotals(IEnumerable<OrderItem> items) =>
        string.Join("\n", items.GroupBy(item => (item.FulfillmentStatus, item.IsBottleOwner))
            .OrderByDescending(group => group.Key.FulfillmentStatus).ThenBy(group => group.Key.IsBottleOwner)
            .Select(group =>
                $"• {OrderItemFulfillmentStatusLabel(group.Key.FulfillmentStatus)} — {group.Sum(item => item.Quantity)} " +
                (group.Key.IsBottleOwner ? "باتل اصلی" : "دکانت") + " (" +
                string.Join("، ", group.GroupBy(item => item.RequestedVolumeMl)
                    .OrderByDescending(size => size.Key)
                    .Select(size => $"{size.Sum(item => item.Quantity)} × {FormatShippingVolume(size.Key)} میل")) + ")"));
}
