using ZibasheERP.Infrastructure.Services;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string ManualSentPrefix = "shipping:manualsent:";
    internal async Task<IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>> ManualAddressButtonsAsync(
        ManualShippingAddresses.Source source, bool sent, CancellationToken ct)
    {
        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>
        {
            new[] { sent ? new TelegramInlineButton("✅ ارسال شد", "shipping:manualdone", Style: "success") :
                new TelegramInlineButton("✅ ارسال شد", ManualSentPrefix + source.ReferenceId.ToString("N")) }
        };
        var tracking = await _db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.Channel == ShippingTrackingNotices.Channel &&
            n.CustomerId == source.Root.CustomerId && n.Recipient == source.Root.ChatId).Select(n => n.Payload).ToArrayAsync(ct);
        var tracked = tracking.Any(p => JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(p)?.SourceMessageId == source.Root.MessageId);
        rows.Add([tracked ? new(ShippingTrackingNotices.Text, "shipping:trackingdone", Style: "success") :
            new("📸 ارسال کد رهگیری", "shipping:trackingcustomer:" + source.Root.CustomerId.ToString("N"))]);
        var receipts = await _db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.CustomerId == source.Root.CustomerId && n.Channel == ShippingPackApproval.ReceiptChannel)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Select(n => n.Payload).ToArrayAsync(ct);
        var last = receipts.Select(p => JsonSerializer.Deserialize<ShippingPackApprovalData>(p)!).FirstOrDefault(p => p.CustomerId == source.Root.CustomerId &&
            p.ShippingChatId == source.Root.ChatId && p.AddressMessageId == source.Root.MessageId && p.RequestId is null && ShippingPackPhotosDelivered(p));
        rows.AddRange(last is null ? ShippingReferenceButtons(source.ReferenceId) : ShippingReferenceButtonsWithPhotoCount(source.ReferenceId, last.PhotoFileIds.Length));
        return rows;
    }

    private async Task<bool> TryHandleManualShippingSentAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Data != "shipping:manualdone" && callback.Data?.StartsWith(ManualSentPrefix, StringComparison.Ordinal) != true) return false;
        if (callback.Message is null || callback.Message.Chat.Id.ToString() != _options.ShippingChatId.Trim() ||
            !await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        { await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true); return true; }
        var sources = await ManualShippingAddresses.SourcesAsync(_db, _options, ct);
        var source = sources.SingleOrDefault(s => s.Root.MessageId == callback.Message.MessageId);
        if (source is null || callback.Data != "shipping:manualdone" && callback.Data != ManualSentPrefix + source.ReferenceId.ToString("N"))
        { await _sender.AnswerCallbackAsync(callback.Id, "این دکمه متعلق به آدرس دستی همین پیام نیست.", ct, true); return true; }
        if (callback.Data == "shipping:manualdone")
        { await _sender.AnswerCallbackAsync(callback.Id, await ManualShippingAddresses.IsSentAsync(_db, source, ct) ? "این بسته قبلاً ارسال‌شده ثبت شده است ✅" : "وضعیت آدرس نیازمند به‌روزرسانی است.", ct, true); return true; }
        await ManualShippingAddresses.CompleteAsync(_db, source, callback.From.Id, "shipping-admin-manual-completion", ct);
        await _sender.AnswerCallbackAsync(callback.Id, "✅ ارسال شد؛ از گزارش آدرس‌های باقی‌مانده خارج شد.", ct, true);
        var rows = await ManualAddressButtonsAsync(source, true, ct);
        await _sender.EditReplyMarkupAsync(source.Root.ChatId, source.Root.MessageId!.Value, rows, ct);
    
        return true;
    }
}
