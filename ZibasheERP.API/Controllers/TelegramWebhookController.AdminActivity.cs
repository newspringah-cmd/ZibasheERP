using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task HandleAdminItemActivityReportAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (!AdminItemActivityReport.CanView(callback.From.Id, callback.Message!.Chat.Id,
                callback.Message.Chat.Type, _options.OwnerUserId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این گزارش فقط در گفت‌وگوی خصوصی مدیر اصلی قابل مشاهده است.", ct, true);
            return;
        }
        var parts = callback.Data!.Split(':');
        var days = 7;
        if (parts.Length is not (2 or 3) || parts[1] != "admin-activity" ||
            (parts.Length == 3 && (!int.TryParse(parts[2], out days) || !AdminItemActivityReport.IsSupportedPeriod(days))))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "بازه گزارش نامعتبر است.", ct, true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var until = DateTime.UtcNow;
        var since = until.AddDays(-days);
        var events = await _db.AdminItemActivities.AsNoTracking().Where(row => !row.IsDeleted &&
            row.Action != AdminItemAction.TrackingStarted && row.CreatedAt >= since && row.CreatedAt < until)
            .ToArrayAsync(ct);
        var trackingSince = await _db.AdminItemActivities.AsNoTracking().Where(row => row.Action == AdminItemAction.TrackingStarted)
            .Select(row => (DateTime?)row.CreatedAt).MinAsync(ct);
        var report = AdminItemActivityReport.Summarize(events, since, until);
        var heading = $"📊 عملکرد ادمین‌ها — {days} روز گذشته\n" +
            $"بازه: {FormatShippingPersianDate(since)} تا {FormatShippingPersianDate(until)}\n" +
            "عملیات موفق روی آیتم‌های لیست فروش و صف باتل\n" +
            (trackingSince.HasValue ? $"شروع ثبت آمار: {FormatShippingPersianDate(trackingSince.Value)}\n" : "") +
            (!trackingSince.HasValue || trackingSince > since ? "⚠️ آمار قبل از فعال‌سازی این قابلیت موجود نیست؛ این بازه پوشش کامل ندارد.\n" : "");
        var lines = report.Select((row, index) =>
            $"{index + 1}. {(!string.IsNullOrWhiteSpace(row.Username) ? "@" + row.Username : string.IsNullOrWhiteSpace(row.DisplayName) ? row.UserId.ToString() : row.DisplayName)}\n" +
            $"شناسه: {row.UserId}\nثبت: {row.Registered} | حذف: {row.Removed} | تغییر میل: {row.VolumeChanged}");
        var body = heading + "\n" + (report.Count == 0 ? "در این بازه عملیاتی ثبت نشده است." : string.Join("\n\n", lines)) +
            $"\n\nمجموع: ثبت {report.Sum(row => row.Registered)} | حذف {report.Sum(row => row.Removed)} | تغییر میل {report.Sum(row => row.VolumeChanged)}";
        foreach (var part in SplitTelegramMessage(body))
            await ReplyAsync(callback.Message.Chat.Id, part, ct);
        await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(), "بازه گزارش عملکرد ادمین‌ها را انتخاب کنید:",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("هفتگی", "invoiceadmin:admin-activity:7"),
                    new TelegramInlineButton("۱۵ روزه", "invoiceadmin:admin-activity:15"),
                    new TelegramInlineButton("یک‌ماهه", "invoiceadmin:admin-activity:30") },
                new[] { new TelegramInlineButton("↩️ منوی اصلی", "invoiceadmin:menu:main") }
            }, ct);
    }
}
