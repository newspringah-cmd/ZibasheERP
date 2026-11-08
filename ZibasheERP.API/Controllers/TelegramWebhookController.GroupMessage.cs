using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task BeginGroupMessageAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!IsPrimaryOwner(callback.From.Id)) { await ReplyAsync(chatId, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct); return; }
        ClearAdminWorkflowDrafts(chatId, callback.From.Id);
        MarketingDrafts[(chatId, callback.From.Id)] = new(MarketingPromotionJob.GroupMessage, DateTime.UtcNow.AddMinutes(20));
        await _sender.SendInlineKeyboardAsync(chatId.ToString(CultureInfo.InvariantCulture),
            "✉️ ارسال پیام از طرف زیباشی به گروه Zibashe perfume\n\nمتن پیام را بفرستید. قبل از ارسال، پیش‌نمایش و دکمهٔ تأیید نمایش داده می‌شود.\nبرای انصراف «لغو» را بفرستید.",
            [[new TelegramInlineButton("↩️ انصراف", "invoiceadmin:menu:main")]], ct);
    }

    private async Task PreviewGroupMessageAsync(TelegramMessage message, MarketingDraft draft, CancellationToken ct)
    {
        if (message.From is null || !IsPrimaryOwner(message.From.Id)) { await ReplyAsync(message.Chat.Id, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct); return; }
        var text = message.Text;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            await ReplyAsync(message.Chat.Id, "یک پیام متنی با حداکثر ۴۰۹۶ نویسه بفرستید؛ عکس و فایل در این گزینه پشتیبانی نمی‌شوند.", ct);
            return;
        }
        var token = Guid.NewGuid().ToString("N")[..8];
        MarketingDrafts[(message.Chat.Id, message.From!.Id)] = draft with {
            MessageText = text, SourceMessageId = message.MessageId, ChoiceToken = token, ExpiresAt = DateTime.UtcNow.AddMinutes(20) };
        await ReplyAsync(message.Chat.Id,
            "👁 پیش‌نمایش پیام در پیام بعدی است. با تأیید، همین متن از طرف زیباشی در گروه Zibashe perfume منتشر می‌شود. برای اصلاح، متن جدید را بفرستید.", ct);
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(CultureInfo.InvariantCulture), text,
            [[new TelegramInlineButton("✅ تأیید و ارسال به گروه", $"invoiceadmin:marketing:group-message-send:{token}")],
             [new TelegramInlineButton("↩️ انصراف", "invoiceadmin:menu:main")]], ct);
    }

    private async Task ConfirmGroupMessageAsync(TelegramCallbackQuery callback, string token, CancellationToken ct)
    {
        var key = (callback.Message!.Chat.Id, callback.From.Id);
        if (!IsPrimaryOwner(callback.From.Id)) { await ReplyAsync(key.Item1, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct); return; }
        if (!MarketingDrafts.TryGetValue(key, out var draft) || draft.Destination != MarketingPromotionJob.GroupMessage ||
            draft.ExpiresAt <= DateTime.UtcNow || draft.ChoiceToken != token || string.IsNullOrWhiteSpace(draft.MessageText))
        {
            await ReplyAsync(key.Item1, "این پیش‌نمایش منقضی یا جایگزین شده است؛ از «ارسال پیام به گروه» متن را دوباره وارد کنید.", ct);
            return;
        }
        var id = MarketingPromotionJob.GroupMessageRequestId(key.Item1, key.Item2, draft.SourceMessageId);
        await using (var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            if (!await _db.NotificationOutbox.AnyAsync(value => value.Id == id, ct))
            {
                _db.NotificationOutbox.Add(new NotificationOutbox {
                    Id = id, CustomerId = Guid.Empty, Channel = MarketingPromotionJob.Channel, EventType = "AdminGroupMessage",
                    Recipient = key.Item1.ToString(CultureInfo.InvariantCulture), CreatedAt = DateTime.UtcNow,
                    Payload = JsonSerializer.Serialize(new MarketingPromotionJob(Guid.Empty, 0, MarketingPromotionJob.GroupMessage,
                        key.Item1, key.Item2, MessageText: draft.MessageText)), Status = NotificationOutboxStatus.Pending });
                await _db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        // Keep a newer text draft if it arrived while this request was being saved.
        ((ICollection<KeyValuePair<(long ChatId, long UserId), MarketingDraft>>)MarketingDrafts)
            .Remove(new(key, draft));
        await SendMarketingStatusAsync(key.Item1, id, "✅ پیام تأیید شد و در صف ارسال به گروه است. نتیجه و لینک پیام همین‌جا نمایش داده می‌شود.", ct);
    }
}
