using System.Reflection;
using ZibasheERP.API.AddressLabels;
using System.Runtime.CompilerServices;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;

public static class PreparedAddressReportTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var rows = Enumerable.Range(1, 125).Select(i => new PreparedAddressReport.Entry(1000 + i, "ليلا بابا زاده", "@receiver_" + i, new DateTime(2026,11,11,10,0,0,DateTimeKind.Utc))).ToArray();
        var pages = Enumerable.Range(0, 3).Select(i => PreparedAddressReport.Build("-1004397297289", rows, i)).ToArray();
        check(pages.All(p => p.Text.Contains("ارسال‌نشده: 125") && p.Pages == 3 && p.Messages.All(m=>m.Length<4096)), "remaining address report counts all registered parcels without a result cap and respects Telegram length limits");
        check(rows.All(r => pages.Sum(p => p.Text.Split("https://t.me/c/4397297289/" + r.MessageId + "\n").Length - 1) == 1), "each remaining address has its exact source link once across report pages");
        check(pages[0].Text.Contains("لیلا بابا زاده — @receiver_1") && !pages[0].Text.Contains("@@"), "remaining address report displays normalized recipient names and customer usernames");
        var last = PreparedAddressReport.Build("-1004397297289", rows, int.MaxValue);
        check(last.Index == 2 && last.Text.Contains("125. ") && PreparedAddressReport.Build("-1004397297289", rows.Take(1).ToArray(), 3).Index == 0,
            "a report page safely clamps after parcels leave the ready queue");
        var empty = PreparedAddressReport.Build("-1004397297289", [], 0);
        check(empty.Pages == 1 && empty.Text.Contains("ارسال‌نشده: 0") && !empty.Text.Contains("https://"), "empty remaining-address queue reports zero without stale address links");
        var duplicate = PreparedAddressReport.Build("-1004397297289", [rows[0], rows[0]], 0);
        check(duplicate.Text.Contains("ارسال‌نشده: 1"), "duplicate context references do not inflate physical-address report counts");
        var longRows = rows.Take(8).Select(r => r with { Name = new string('ن', 500), Username = new string('a', 500) }).ToArray();
        check(PreparedAddressReport.Build("-1004397297289", longRows, 0).Messages.All(m=>m.Length<4096), "unusually long recipient fields cannot overflow a Telegram report page");
        check(pages.All(p=>p.Text.Contains("📅 تاریخ ثبت: ۱۴۰۵/۰۸/۲۰")),"remaining-address report shows the actual registration date in Persian calendar and Persian digits");
        check(ShippingAddressDisplay.RegisteredDate(new DateTime(2026,3,20,21,0,0,DateTimeKind.Unspecified))=="۱۴۰۵/۰۱/۰۱",
            "registration date uses Tehran midnight and Nowruz correctly for SQL UTC timestamps without a DateTime kind");
        const string body="📦 درخواست ارسال دستی\n\nآیدی: @example\nنام و آدرس اصلی\n🔴 تعداد آیتم‌ها: 2";
        var dated=ShippingAddressDisplay.WithRegistrationDate(body,new DateTime(2026,11,11,10,0,0));
        check(dated.Replace("📅 تاریخ ثبت: ۱۴۰۵/۰۸/۲۰\n",string.Empty)==body&&ShippingAddressDisplay.WithRegistrationDate(dated,new DateTime(2026,11,11,10,0,0))==dated,
            "adding registration date preserves the complete original address body and never duplicates the date on refresh");
        check(pages[0].Text.Split("https://t.me/c/").Length-1==50&&pages[1].Text.Split("https://t.me/c/").Length-1==50&&pages[2].Text.Split("https://t.me/c/").Length-1==25,
            "remaining address report shows exactly fifty addresses per logical page and keeps the final remainder");
        check(pages.Any(p=>p.Messages.Length>1)&&pages.All(p=>p.Messages.All(m=>m.Length<4096))&&rows.All(r=>pages.SelectMany(p=>p.Messages).Count(m=>m.Contains("https://t.me/c/4397297289/"+r.MessageId+"\n"))==1),
            "large fifty-address pages split safely into Telegram messages without losing or splitting an address link");
        var sender = DispatchProxy.Create<ITelegramMessageSender, PreparedReportSender>();
        var capture = (PreparedReportSender)(object)sender;
        var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        typeof(TelegramWebhookController).GetField("_sender", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, sender);
        var method = typeof(TelegramWebhookController).GetMethod("TryHandlePreparedAddressReportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var chat in new[] { new TelegramChat(-1004397297289, "supergroup"), new TelegramChat(124, "private") })
        {
            var cb = new TelegramCallbackQuery("fixture", new(123, null), new TelegramMessage(null, chat, new(123, null), null, MessageId: 1), "shipping:remaining:open");
            await (Task<bool>)method.Invoke(controller, [cb, CancellationToken.None])!;
        }
        check(capture.Denied == 2, "remaining-address report rejects group callbacks and mismatched private recipients before loading customer data");
    }
}
public class PreparedReportSender : DispatchProxy
{
    public int Denied;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "AnswerCallbackAsync") throw new InvalidOperationException("Rejected report cannot send customer data");
        Denied++;
        return Task.FromResult(new TelegramSendResult(true));
    }
}
