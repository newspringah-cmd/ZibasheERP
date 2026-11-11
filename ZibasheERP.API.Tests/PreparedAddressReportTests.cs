using System.Reflection;
using System.Runtime.CompilerServices;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;

public static class PreparedAddressReportTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var rows = Enumerable.Range(1, 25).Select(i => new PreparedAddressReport.Entry(1000 + i, "ليلا بابا زاده", "@receiver_" + i)).ToArray();
        var pages = Enumerable.Range(0, 4).Select(i => PreparedAddressReport.Build("-1004397297289", rows, i)).ToArray();
        check(pages.All(p => p.Text.Contains("ارسال‌نشده: 25") && p.Pages == 4 && p.Text.Length < 4096), "remaining address report counts all registered parcels without a result cap and respects Telegram length limits");
        check(rows.All(r => pages.Sum(p => p.Text.Split("https://t.me/c/4397297289/" + r.MessageId + "\n").Length - 1) == 1), "each remaining address has its exact source link once across report pages");
        check(pages[0].Text.Contains("لیلا بابا زاده — @receiver_1") && !pages[0].Text.Contains("@@"), "remaining address report displays normalized recipient names and customer usernames");
        var last = PreparedAddressReport.Build("-1004397297289", rows, int.MaxValue);
        check(last.Index == 3 && last.Text.Contains("25. ") && PreparedAddressReport.Build("-1004397297289", rows.Take(1).ToArray(), 3).Index == 0,
            "a report page safely clamps after parcels leave the ready queue");
        var empty = PreparedAddressReport.Build("-1004397297289", [], 0);
        check(empty.Pages == 1 && empty.Text.Contains("ارسال‌نشده: 0") && !empty.Text.Contains("https://"), "empty remaining-address queue reports zero without stale address links");
        var duplicate = PreparedAddressReport.Build("-1004397297289", [rows[0], rows[0]], 0);
        check(duplicate.Text.Contains("ارسال‌نشده: 1"), "duplicate context references do not inflate physical-address report counts");
        var longRows = rows.Take(8).Select(r => r with { Name = new string('ن', 500), Username = new string('a', 500) }).ToArray();
        check(PreparedAddressReport.Build("-1004397297289", longRows, 0).Text.Length < 4096, "unusually long recipient fields cannot overflow a Telegram report page");
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
