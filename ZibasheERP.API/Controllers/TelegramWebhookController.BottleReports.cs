using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task SendLowStockBottleReportAsync(long chatId, CancellationToken ct)
    {
        var lists = await _db.SalesLists.AsNoTracking()
            .Where(list => !list.IsDeleted && !list.IsInventoryOffer &&
                list.Status == SalesListStatus.Open &&
                list.ReservedVolume < list.TotalVolume &&
                (long)list.ReservedVolume * 100 > (long)list.TotalVolume * 70 &&
                !_db.InvoiceIssuanceBatchSalesLists.Any(link => link.SalesListId == list.Id))
            .Include(list => list.Requests.Where(request => !request.IsDeleted &&
                request.Kind == SalesListRequestKind.CurrentBottle &&
                (request.Status == SalesListRequestStatus.Confirmed ||
                 request.Status == SalesListRequestStatus.Promoted ||
                 request.Status == SalesListRequestStatus.QueuedForInvoice)))
                .ThenInclude(request => request.Bottle)
            .OrderBy(list => list.PublicCode)
            .ToArrayAsync(ct);

        static string BottleDescription(SalesListRequest request) => request.Bottle is { } bottle
            ? $"{bottle.Name} — {bottle.VolumeMl} میل — {(bottle.Type == BottleType.Fancy ? "فانتزی" : "نرمال")}"
            : $"نوع شیشه ثبت نشده — درخواست {request.VolumeMl} میل";

        static string Counts(IEnumerable<SalesListRequest> requests) => string.Join("\n",
            requests.GroupBy(BottleDescription).OrderBy(group => group.Key)
                .Select(group => $"• {group.Key}: {group.Count()} عدد"));

        var bottles = lists.SelectMany(list => list.Requests).Where(request => !request.IsBottleOwner).ToArray();
        var sections = new List<string>
        {
            "🧴 گزارش شیشه‌های لیست‌های بیش از ۷۰٪ تکمیل\n" +
            "لیست‌های باز و هنوز تکمیل‌نشده با بیش از ۷۰٪ حجم رزروشده\n\n" +
            $"تعداد لیست: {lists.Length}\nتعداد شیشه: {bottles.Length}\n\n" +
            (bottles.Length == 0 ? "شیشه‌ای برای این لیست‌ها ثبت نشده است." : "جمع تفکیکی:\n" + Counts(bottles))
        };
        sections.Add("سهم صاحب باتل و درخواست‌های صف بعدی در شمارش شیشه‌ها لحاظ نشده‌اند.");
        foreach (var part in SplitTelegramMessage(string.Join("\n\n", sections)))
            await ReplyAsync(chatId, part, ct);
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), "گزارش شیشه‌ها",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("↩️ گزارش مالی", "invoiceadmin:pending-invoice-total") }
            }, ct);
    }
}
