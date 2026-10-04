using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task SendTelegramGroupConnectionReportAsync(long chatId, string data, CancellationToken ct)
    {
        const int pageSize = 10;
        var customers = _db.Customers.AsNoTracking().Where(customer => !customer.IsDeleted);
        var groups = _db.CustomerTelegramGroups.AsNoTracking()
            .Where(group => !group.IsDeleted && !group.Customer.IsDeleted);
        var unmapped = customers.Where(customer => !groups.Any(group => group.CustomerId == customer.Id));
        var inactive = groups.Where(group => !group.IsActive);
        var unmappedCount = await unmapped.CountAsync(ct);
        var inactiveCount = await inactive.CountAsync(ct);
        var buttons = new List<TelegramInlineButton[]>();
        var parts = data.Split(':');
        var kind = parts.Length == 4 ? parts[2] : string.Empty;
        var message = $"🔗 گزارش اتصال گروه‌های مشتریان\n\n👤 مشتری بدون اتصال: {unmappedCount}\n⛔ اتصال گروه غیرفعال: {inactiveCount}";

        if ((kind == "unmapped" || kind == "inactive") &&
            int.TryParse(parts[3], out var requestedPage) && requestedPage >= 0)
        {
            var total = kind == "unmapped" ? unmappedCount : inactiveCount;
            var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            var page = Math.Min(requestedPage, pageCount - 1);
            var offset = page * pageSize;
            string[] lines;
            if (kind == "unmapped")
            {
                var rows = await unmapped.OrderBy(customer => customer.FullName).ThenBy(customer => customer.Id)
                    .Skip(offset).Take(pageSize)
                    .Select(customer => new { customer.FullName, customer.Username, customer.TelegramId,
                        InvoiceNumber = _db.Invoices.Where(invoice => !invoice.IsDeleted && invoice.Order != null &&
                            !invoice.Order.IsDeleted && invoice.Order.CustomerId == customer.Id)
                            .OrderByDescending(invoice => invoice.IssuedAt).ThenBy(invoice => invoice.Id)
                            .Select(invoice => invoice.InvoiceNumber).FirstOrDefault() })
                    .ToArrayAsync(ct);
                lines = rows.Select((row, index) =>
                    $"{offset + index + 1}. {row.FullName}\n" +
                    $"یوزرنیم: {GroupReportUsername(row.Username)}\nشناسه تلگرام: {row.TelegramId ?? "ثبت نشده"}\n" +
                    GroupReportConnectInstruction(row.InvoiceNumber, row.Username))
                    .ToArray();
            }
            else
            {
                var rows = await inactive.OrderBy(group => group.Customer.FullName).ThenBy(group => group.Id)
                    .Skip(offset).Take(pageSize)
                    .Select(group => new { group.Customer.FullName, CustomerUsername = group.Customer.Username,
                        group.Title, group.ChatId, group.LastSeenAt,
                        InvoiceNumber = _db.Invoices.Where(invoice => !invoice.IsDeleted && invoice.Order != null &&
                            !invoice.Order.IsDeleted && invoice.Order.CustomerId == group.CustomerId)
                            .OrderByDescending(invoice => invoice.IssuedAt).ThenBy(invoice => invoice.Id)
                            .Select(invoice => invoice.InvoiceNumber).FirstOrDefault() })
                    .ToArrayAsync(ct);
                lines = rows.Select((row, index) =>
                    $"{offset + index + 1}. {row.FullName}\n" +
                    $"یوزرنیم: {GroupReportUsername(row.CustomerUsername)}\nگروه: {row.Title}\n" +
                    $"شناسه گروه: {row.ChatId}\n" +
                    GroupReportConnectInstruction(row.InvoiceNumber, row.CustomerUsername, inactive: true) +
                    $"\nآخرین مشاهده ربات (UTC): {row.LastSeenAt?.ToString("yyyy-MM-dd HH:mm") ?? "ثبت نشده"}")
                    .ToArray();
            }
            var title = kind == "unmapped" ? "👤 مشتری‌های بدون اتصال گروه" : "⛔ اتصال‌های گروه غیرفعال";
            message = $"{title}\nتعداد: {total} | صفحه {page + 1}/{pageCount}\n\n" +
                (lines.Length == 0 ? "موردی وجود ندارد." : string.Join("\n\n", lines));
            var navigation = new List<TelegramInlineButton>();
            if (page > 0)
                navigation.Add(new("⬅️ قبلی", $"invoiceadmin:group-report:{kind}:{page - 1}"));
            if (page + 1 < pageCount)
                navigation.Add(new("بعدی ➡️", $"invoiceadmin:group-report:{kind}:{page + 1}"));
            if (navigation.Count > 0)
                buttons.Add(navigation.ToArray());
            buttons.Add([new("🔄 تازه‌سازی", $"invoiceadmin:group-report:{kind}:{page}")]);
            buttons.Add([new("↩️ گزارش اتصال گروه‌ها", "invoiceadmin:group-report")]);
        }
        else
        {
            buttons.Add([new($"👤 مشتری‌های بدون اتصال ({unmappedCount})", "invoiceadmin:group-report:unmapped:0")]);
            buttons.Add([new($"⛔ گروه‌های غیرفعال ({inactiveCount})", "invoiceadmin:group-report:inactive:0")]);
            buttons.Add([new("🔄 تازه‌سازی", "invoiceadmin:group-report")]);
        }
        buttons.Add([new("↩️ تنظیمات", "invoiceadmin:menu:settings")]);
        foreach (var chunk in SplitTelegramMessage(message).SkipLast(1))
            await ReplyAsync(chatId, chunk, ct);
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), SplitTelegramMessage(message).Last(), buttons, ct);
    }

    private static string GroupReportUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "ثبت نشده" : "@" + username.Trim().TrimStart('@');

    private static string GroupReportConnectInstruction(string? invoiceNumber, string? username, bool inactive = false)
    {
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
            return "دستور اتصال (توسط مدیر در گروه همین مشتری):\n" +
                $"/connect {invoiceNumber}\n" +
                (inactive ? "ابتدا مطمئن شوید ربات در همین گروه حضور دارد.\n" : string.Empty) +
                "با اتصال، مدارک ارسال‌نشده ممکن است در صف ارسال قرار بگیرند.";
        if (inactive)
            return "فاکتور اتصال موجود نیست؛ برای بررسی اتصال در همین گروه توسط مدیر مجاز:\n/ad";
        if (!string.IsNullOrWhiteSpace(username))
            return "در گروه همین مشتری، توسط مدیر یا حسابدار مجاز:\n/ad\n" +
                $"سپس در پاسخ به درخواست یوزرنیم بفرستید:\n{GroupReportUsername(username)}";
        return "فاکتور و یوزرنیم اتصال موجود نیست؛ ابتدا هویت مشتری تکمیل شود.";
    }
}
