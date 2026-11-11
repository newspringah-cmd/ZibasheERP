using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string PreparedAddressReportPrefix = "shipping:remaining:";

    private async Task<bool> TryHandlePreparedAddressReportAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Data?.StartsWith(PreparedAddressReportPrefix, StringComparison.Ordinal) != true) return false;
        var actor = callback.From.Id;
        if (callback.Message is null || callback.Message.Chat.Type != "private" || callback.Message.Chat.Id != actor ||
            !await IsAuthorizedInvoiceActionAdminAsync(actor, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این گزارش فقط برای ادمین‌های مجاز در خصوصی ربات است.", ct, true);
            return true;
        }
        var action = callback.Data[PreparedAddressReportPrefix.Length..];
        var first = action == "open";
        if (!first && (!int.TryParse(action, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "صفحهٔ گزارش معتبر نیست.", ct, true);
            return true;
        }
        if (string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "گروه آماده‌سازی بسته تنظیم نشده است.", ct, true);
            return true;
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var roots = await TrackingPreparedAddresses.LoadForNameWarningsAsync(_db, _options.ShippingChatId.Trim(), ct);
        var addressIds = roots.Select(r => r.AddressId).Distinct().ToArray();
        var addresses = await _db.Addresses.AsNoTracking().Where(a => addressIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var entries = roots.OrderBy(r => r.PostedAt).ThenBy(r => r.MessageId).Select(r => new PreparedAddressReport.Entry(
            r.MessageId, addresses.TryGetValue(r.AddressId, out var address) ? ShippingAddressDisplay.RecipientName(address) ?? "نام ثبت نشده" : "نام ثبت نشده",
            r.Username)).ToArray();
        var page = PreparedAddressReport.Build(_options.ShippingChatId.Trim(), entries,
            first ? 0 : int.Parse(action, CultureInfo.InvariantCulture));
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        var navigation = new List<TelegramInlineButton>();
        if (page.Index > 0) navigation.Add(new("⬅️ قبلی", PreparedAddressReportPrefix + (page.Index - 1)));
        if (page.Index + 1 < page.Pages) navigation.Add(new("بعدی ➡️", PreparedAddressReportPrefix + (page.Index + 1)));
        if (navigation.Count > 0) buttons.Add(navigation);
        buttons.Add([new("🔄 به‌روزرسانی گزارش", PreparedAddressReportPrefix + page.Index)]);
        buttons.Add([new("↩️ منوی مدیریت", "invoiceadmin:menu:main")]);
        if (first) await _sender.SendInlineKeyboardAsync(actor.ToString(), page.Text, buttons, ct);
        else
        {
            var result = await _sender.EditTextWithKeyboardAsync(actor.ToString(), callback.Message.MessageId, page.Text, buttons, ct);
            if (!result.IsSuccessful && !IsTelegramMessageUnchanged(result.Error))
                await _sender.SendAsync(actor.ToString(), "به‌روزرسانی انجام نشد؛ گزارش را دوباره از منو باز کنید.", ct);
        }
        return true;
    }
}
