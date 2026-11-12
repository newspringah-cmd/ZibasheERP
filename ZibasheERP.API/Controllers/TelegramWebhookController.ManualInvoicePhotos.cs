using System.Net;
using ZibasheERP.API.Telegram;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task PreviewManualInvoicePhotoAsync(TelegramManualInvoiceDraft draft, string fileId, CancellationToken ct)
    {
        Guid token; int index;
        lock (draft)
        {
            index = draft.ProductPhotoFileIds.Count;
            if (draft.Stage != TelegramManualInvoiceStage.AwaitingPhoto || index >= draft.Lines.Count) return;
            token = Guid.NewGuid();
            draft.PendingProductPhotoFileId = fileId;
            draft.PendingPhotoToken = token;
            draft.PendingPhotoMessageId = null;
            draft.Stage = TelegramManualInvoiceStage.AwaitingPhotoConfirmation;
        }
        _manualInvoiceDrafts.Set(draft);
        var line = draft.Lines[index];
        var result = await _sender.SendPhotoWithKeyboardAsync(draft.ChatId.ToString(), fileId,
            (draft.IsInventory ? "🔴🔴 موجودی 🔴🔴\n" : "") +
            $"🔎 <b>بررسی عکس آیتم {index + 1} از {draft.Lines.Count}</b>\n" +
            $"مشتری: {WebUtility.HtmlEncode(draft.CustomerIdentity)}\n" +
            $"عطر: <b>{WebUtility.HtmlEncode(line.Description)}</b>\nمقدار: {line.Quantity} میل\n\n" +
            "آیا عکس بالا متعلق به همین عطر است؟ پس از تأیید عکس می‌توانید ادامه دهید.",
            [[new("✅ تأیید عکس", $"invoicebatch:photook:{token:N}"),
              new("🔄 تغییر عکس", $"invoicebatch:photoredo:{token:N}")]], ct);
        lock (draft)
        {
            if (draft.PendingPhotoToken != token || draft.Stage != TelegramManualInvoiceStage.AwaitingPhotoConfirmation) return;
            if (result.IsSuccessful && result.MessageId is > 0) draft.PendingPhotoMessageId = result.MessageId;
            else
            {
                draft.PendingProductPhotoFileId = null;
                draft.PendingPhotoToken = Guid.Empty;
                draft.Stage = TelegramManualInvoiceStage.AwaitingPhoto;
            }
        }
        if (!result.IsSuccessful || result.MessageId is not > 0)
            await ReplyAsync(draft.ChatId, "پیش‌نمایش عکس تأیید نشد؛ عکس را دوباره ارسال کنید. هنوز هیچ فاکتوری صادر نشده است.", ct);
    }

    private async Task<bool> TryHandleManualInvoicePhotoCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var confirm = data.StartsWith("invoicebatch:photook:", StringComparison.Ordinal);
        var replace = data.StartsWith("invoicebatch:photoredo:", StringComparison.Ordinal);
        if (!confirm && !replace) return false;
        var valid = false;
        if (callback.Message is not null && Guid.TryParseExact(data.Split(':').Last(), "N", out var token) &&
            _manualInvoiceDrafts.TryGet(callback.Message.Chat.Id, callback.From.Id, out var draft))
        {
            lock (draft)
            {
                valid = draft.Stage == TelegramManualInvoiceStage.AwaitingPhotoConfirmation &&
                    draft.PendingPhotoToken == token && draft.PendingPhotoMessageId == callback.Message.MessageId &&
                    !string.IsNullOrWhiteSpace(draft.PendingProductPhotoFileId) && draft.ProductPhotoFileIds.Count < draft.Lines.Count;
                if (valid)
                {
                    if (confirm) draft.ProductPhotoFileIds.Add(draft.PendingProductPhotoFileId!);
                    draft.PendingProductPhotoFileId = null;
                    draft.PendingPhotoMessageId = null;
                    draft.PendingPhotoToken = Guid.Empty;
                    draft.Stage = TelegramManualInvoiceStage.AwaitingPhoto;
                }
            }
            if (valid)
            {
                await _sender.AnswerCallbackAsync(callback.Id, confirm ? "عکس این عطر تأیید شد ✅" : "عکس صحیح همین عطر را بفرستید.", ct);
                if (!_manualInvoiceDrafts.TryGet(draft.ChatId, draft.UserId, out var current) || !ReferenceEquals(current, draft)) return true;
                if (replace) await ReplyAsync(draft.ChatId, $"عکس صحیح این عطر را ارسال کنید:\n{draft.Lines[draft.ProductPhotoFileIds.Count].Description}", ct);
                else await AdvanceManualInvoicePhotoAsync(draft, ct);
                return true;
            }
        }
        await _sender.AnswerCallbackAsync(callback.Id, "این تأیید عکس منقضی شده یا متعلق به همین مرحله و حساب نیست.", ct, true);
        return true;
    }

    private async Task AdvanceManualInvoicePhotoAsync(TelegramManualInvoiceDraft draft, CancellationToken ct)
    {

            if (draft.ProductPhotoFileIds.Count < draft.Lines.Count)
            {
                var nextIndex = draft.ProductPhotoFileIds.Count;
                _manualInvoiceDrafts.Set(draft);
                await ReplyAsync(draft.ChatId,
                    $"عکس آیتم {nextIndex + 1} از {draft.Lines.Count} را ارسال کنید:\n{draft.Lines[nextIndex].Description}", ct);
                return;
            }
            static string IdentityKey(string identity) => identity.Trim().TrimStart('@').ToLowerInvariant();
            var recipient = draft.IsGift ? draft.GiftRecipientIdentity : null;
            var entry = draft.Entries.FirstOrDefault(value =>
                IdentityKey(value.CustomerIdentity) == IdentityKey(draft.CustomerIdentity) &&
                IdentityKey(value.GiftRecipientIdentity ?? "") == IdentityKey(recipient ?? ""));
            if (entry is null)
            {
                entry = new TelegramManualInvoiceEntry
                {
                    CustomerIdentity = draft.CustomerIdentity, GiftRecipientIdentity = recipient
                };
                draft.Entries.Add(entry);
            }
            entry.Lines.AddRange(draft.Lines);
            entry.Photos.AddRange(draft.ProductPhotoFileIds);
            draft.Stage = TelegramManualInvoiceStage.AwaitingMoreLines;
            _manualInvoiceDrafts.Set(draft);
            var total = draft.Lines.Sum(line => (line.TotalAmount ?? line.Quantity * line.UnitAmount) + line.BottleAmount);
            await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
                (draft.IsInventory ? "🔴🔴 موجودی 🔴🔴\n" : string.Empty) +
                $"پیش‌نمایش فاکتور دستی\n" +
                (draft.IsGift
                    ? $"هدیه‌دهنده: {draft.CustomerIdentity}\nهدیه‌گیرنده: {draft.GiftRecipientIdentity}\n"
                    : $"مشتری: {draft.CustomerIdentity}\n") +
                $"تعداد ردیف: {draft.Lines.Count}\nمبلغ کل: {total:N0} تومان\nعکس همه آیتم‌ها: بررسی و تأیید شد ✅",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[] { new TelegramInlineButton("➕ ادامه برای فاکتور جدید", "invoicebatch:manualadd") },
                    new[] { new TelegramInlineButton("✅ پایان", "invoicebatch:manualfinish") },
                    new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:manualcancel") }
                }, ct);
    }
}
