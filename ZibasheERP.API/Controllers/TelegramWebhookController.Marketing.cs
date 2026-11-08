using System.Collections.Concurrent;
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
    private sealed record MarketingDraft(string Destination, DateTime ExpiresAt, int? Code = null,
        Guid? SalesListId = null, long SourceMessageId = 0, string? ChoiceToken = null, string? MessageText = null, string CopyStage = "code");
    private static readonly ConcurrentDictionary<(long ChatId, long UserId), MarketingDraft> MarketingDrafts = new();

    private async Task HandleMarketingCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت تبلیغات ندارید.", ct, true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var action = callback.Data!["invoiceadmin:marketing:".Length..];
        if ((action == MarketingPromotionJob.GroupMessage || action.StartsWith("group-message-send:", StringComparison.Ordinal)) && !IsPrimaryOwner(callback.From.Id))
        {
            MarketingDrafts.TryRemove((chatId, callback.From.Id), out _);
            await ReplyAsync(chatId, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct);
            return;
        }
        if (action.StartsWith("status:", StringComparison.Ordinal) && Guid.TryParseExact(action[7..], "N", out var id))
        {
            var job = await _db.NotificationOutbox.AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == id && value.Channel == MarketingPromotionJob.Channel && !value.IsDeleted, ct);
            var payload = job is null ? null : JsonSerializer.Deserialize<MarketingPromotionJob>(job.Payload);
            if (payload is null || payload.AdminChatId != chatId)
            {
                await ReplyAsync(chatId, "درخواست پیدا نشد.", ct);
                return;
            }
            if (payload.Destination == MarketingPromotionJob.GroupMessage && !IsPrimaryOwner(callback.From.Id))
            {
                await ReplyAsync(chatId, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct);
                return;
            }
            var status = job!.Status switch
            {
                NotificationOutboxStatus.Processed => payload.Destination == MarketingPromotionJob.RemoveChannel
                    ? "✅ تبلیغ‌های کانال حذف شدند و تبلیغ خودکار کانال متوقف است." : payload.Destination == "inventory-publish"
                    ? "✅ موجودی منتشر شد؛ محدودیت زمانی ندارد و تا فروش و صدور فاکتور باقی می‌ماند." : payload.Destination == "inventory-report"
                    ? "✅ گزارش فاکتور موجودی به گروه واریز جدید ارسال شد." : payload.Destination == MarketingPromotionJob.GroupMessage ? "✅ پیام شما در گروه ارسال شد." : "✅ تبلیغ ارسال شد.",
                NotificationOutboxStatus.Failed => "⚠️ " + (job.LastError ?? "ارسال انجام نشد."),
                NotificationOutboxStatus.Processing => "⏳ درخواست در حال آماده‌سازی و ارسال است.",
                _ => "⏳ درخواست در صف ارسال است."
            };
            await SendMarketingStatusAsync(chatId, id, (payload.Destination == "inventory-publish" ? $"انتشار موجودی {payload.Code}" : payload.Destination == "inventory-report" ? $"گزارش فاکتور موجودی {payload.Code}" : payload.Destination == MarketingPromotionJob.RemoveChannel ? "حذف تبلیغ کانال" : payload.Destination == MarketingPromotionJob.GroupMessage ? "ارسال پیام به گروه" : $"لیست {payload.Code}") + $"\n{status}" +
                (payload.ResultUrl is null ? "" : "\n" + payload.ResultUrl), ct,
                payload.Destination is "inventory-publish" or "inventory-report");
            return;
        }
        if (action == MarketingPromotionJob.GroupMessage)
        {
            await BeginGroupMessageAsync(callback, ct);
            return;
        }
        if (action.StartsWith("group-message-send:", StringComparison.Ordinal))
        {
            await ConfirmGroupMessageAsync(callback, action["group-message-send:".Length..], ct);
            return;
        }
        if(action.StartsWith("copy-mode:",StringComparison.Ordinal)||action.StartsWith("copy-send:",StringComparison.Ordinal))
        {await HandleMarketingCopyCallbackAsync(callback,action,ct);return;}
        if (action.StartsWith("channel-mode:", StringComparison.Ordinal))
        {
            var parts = action.Split(':');
            var key = (chatId, callback.From.Id);
            if (parts.Length != 3 || parts[2] is not ("append" or "replace") ||
                !MarketingDrafts.TryGetValue(key, out var draft) || draft.Destination != "channel" || draft.CopyStage != "channel-mode" ||
                draft.ExpiresAt <= DateTime.UtcNow || draft.ChoiceToken != parts[1] || !draft.Code.HasValue || !draft.SalesListId.HasValue)
            {
                await ReplyAsync(chatId, "این انتخاب منقضی شده است؛ از «تبلیغ در کانال» کد لیست را دوباره وارد کنید.", ct);
                return;
            }
            var available = await _db.SalesLists.AsNoTracking().AnyAsync(value => value.Id == draft.SalesListId &&
                !value.IsDeleted && !value.IsInventoryOffer && value.Status == SalesListStatus.Open &&
                value.TotalVolume > value.ReservedVolume && (value.StablePublicCode ?? value.PublicCode) == draft.Code &&
                value.TelegramMessageId != null && value.TelegramChannelId != null && value.TelegramPhotoFileId != null, ct);
            if (!available)
            {
                MarketingDrafts.TryRemove(key, out _);
                await ReplyAsync(chatId, "این لیست دیگر برای تبلیغ باز نیست؛ کد لیست دیگری وارد کنید.", ct);
                return;
            }
            if(!MarketingDrafts.TryGetValue(key,out var latest)||latest!=draft)
            {await ReplyAsync(chatId,"این انتخاب منقضی یا جایگزین شده است؛ از دکمه‌های آخرین پیش‌نمایش استفاده کنید.",ct);return;}
            await QueueMarketingPromotionAsync(chatId, callback.From.Id, draft.SourceMessageId, draft.Code.Value,
                draft.SalesListId.Value, "channel", parts[2], ct, draft.MessageText);
            MarketingDrafts.TryRemove(key, out _);
            return;
        }
        if (action == "delete-channel")
        {
            ClearAdminWorkflowDrafts(chatId, callback.From.Id);
            await _sender.SendInlineKeyboardAsync(chatId.ToString(),
                "🗑 حذف تبلیغ کانال\n\nهمهٔ تبلیغ‌های ثبت‌شدهٔ کانال حذف می‌شوند و ارسال خودکار کانال متوقف می‌ماند. با انتخاب دوبارهٔ «تبلیغ در کانال» و واردکردن کد لیست، ارسال از سر گرفته می‌شود.\n\nتبلیغ‌های کانال حذف شوند؟",
                [[new TelegramInlineButton("🗑 بله، حذف تبلیغ‌های کانال", "invoiceadmin:marketing:delete-channel:confirm")],
                 [new TelegramInlineButton("↩️ انصراف", "invoiceadmin:marketing:menu")]], ct);
            return;
        }
        if (action == "delete-channel:confirm")
        {
            ClearAdminWorkflowDrafts(chatId, callback.From.Id);
            await QueueChannelRemovalAsync(callback, ct);
            return;
        }
        if (MarketingPromotionJob.IsDestination(action))
        {
            ClearAdminWorkflowDrafts(chatId, callback.From.Id);
            MarketingDrafts[(chatId, callback.From.Id)] = new(action, DateTime.UtcNow.AddMinutes(20));
            await _sender.SendInlineKeyboardAsync(chatId.ToString(),
                (action == "group" ? "👥 تبلیغ در گروه" : "📢 تبلیغ در کانال — ۲۴ ساعت") +
                "\n\nکد لیست عطر را بفرستید؛ مثال: ۹۰۵۶\n" +
                (action == "group" ? "بعد از کد، متن توسط زیبا یا متن پیشنهادی خودتان را انتخاب می‌کنید." : "بعد از انتخاب متن، مشخص می‌کنید تبلیغ‌های قبلی بمانند یا حذف شوند."),
                [[new TelegramInlineButton("↩️ بازگشت به تبلیغات", "invoiceadmin:marketing:menu")]], ct);
            return;
        }
        ClearAdminWorkflowDrafts(chatId, callback.From.Id);
        var menuButtons = new List<IReadOnlyCollection<TelegramInlineButton>> {
            new[] { new TelegramInlineButton("👥 تبلیغ در گروه", "invoiceadmin:marketing:group") },
            new[] { new TelegramInlineButton("📢 تبلیغ در کانال", "invoiceadmin:marketing:channel") },
            new[] { new TelegramInlineButton("🗑 حذف تبلیغ‌های کانال", "invoiceadmin:marketing:delete-channel") }
        };
        if (IsPrimaryOwner(callback.From.Id))
        {
            menuButtons.Add(new[] { new TelegramInlineButton("✉️ ارسال پیام به گروه", "invoiceadmin:marketing:group-message") });
            menuButtons.Add(new[] { new TelegramInlineButton("⏳ پروموشن ساعتی", "invoiceadmin:flash:menu") });
        }
        menuButtons.Add(new[] { new TelegramInlineButton("↩️ منوی اصلی", "invoiceadmin:menu:main") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "📣 تبلیغات\n\nمقصد را انتخاب کنید و سپس کد لیست را بفرستید.\nارسال گروه جدا از برنامهٔ خودکار است. برای کانال می‌توانید قبلی‌ها را نگه دارید یا جایگزین کنید. هر تبلیغ جداگانه تا ۲۴ ساعت فعال است و با تکمیل لیست زودتر حذف می‌شود.",
            menuButtons, ct);
    }

    private async Task QueueChannelRemovalAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var id = MarketingPromotionJob.RemovalRequestId(chatId, callback.From.Id, callback.Id);
        var chat = chatId.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var prior = await _db.NotificationOutbox.FirstOrDefaultAsync(value => !value.IsDeleted &&
                value.Channel == MarketingPromotionJob.Channel && (value.Id == id ||
                (value.EventType == "ChannelPromotionRemoval" && value.Recipient == chat &&
                 (value.Status == NotificationOutboxStatus.Pending || value.Status == NotificationOutboxStatus.Processing))), ct);
            if (prior is not null) id = prior.Id;
            else
            {
                _db.NotificationOutbox.Add(new NotificationOutbox
                {
                    Id = id, CustomerId = Guid.Empty, Channel = MarketingPromotionJob.Channel,
                    EventType = "ChannelPromotionRemoval", Recipient = chat, CreatedAt = now,
                    Payload = JsonSerializer.Serialize(new MarketingPromotionJob(Guid.Empty, 0,
                        MarketingPromotionJob.RemoveChannel, chatId, callback.From.Id)),
                    Status = NotificationOutboxStatus.Pending
                });
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await SendMarketingStatusAsync(chatId, id,
            "✅ درخواست حذف تبلیغ کانال ثبت شد. نتیجه همین‌جا اعلام می‌شود.\nارسال خودکار کانال تا انتخاب دوبارهٔ تبلیغ کانال متوقف خواهد شد.", ct);
    }

    private Task SendMarketingStatusAsync(long chatId, Guid id, string text, CancellationToken ct, bool inventory=false) =>
        _sender.SendInlineKeyboardAsync(chatId.ToString(), text,
            [[new TelegramInlineButton("🔄 وضعیت درخواست", $"invoiceadmin:marketing:status:{id:N}")],
             [new TelegramInlineButton(inventory?"↩️ موجودی‌ها":"↩️ تبلیغات", inventory?InvAction("menu"):"invoiceadmin:marketing:menu")]], ct);

    private async Task<bool> TryHandleMarketingMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !MarketingDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft)) return false;
        var key = (message.Chat.Id, message.From.Id);
        if (draft.ExpiresAt <= DateTime.UtcNow) { MarketingDrafts.TryRemove(key, out _); return false; }
        var text = message.Text?.Trim();
        if (text is "/cancel" or "لغو")
        {
            MarketingDrafts.TryRemove(key, out _);
            await ReplyAsync(message.Chat.Id, draft.Destination == MarketingPromotionJob.GroupMessage ? "ارسال پیام به گروه لغو شد." : "آماده‌سازی تبلیغ لغو شد.", ct);
            return true;
        }
        if (text?.StartsWith('/') == true) return false;
        if (!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id, ct))
        {
            MarketingDrafts.TryRemove(key, out _);
            await ReplyAsync(message.Chat.Id, "دسترسی مدیریت تبلیغات ندارید.", ct);
            return true;
        }
        if (draft.Destination == MarketingPromotionJob.GroupMessage)
        {
            if (!IsPrimaryOwner(message.From.Id))
            {
                MarketingDrafts.TryRemove(key, out _);
                await ReplyAsync(message.Chat.Id, "این گزینه فقط برای مدیر @zahraa_frj فعال است.", ct);
                return true;
            }
            await PreviewGroupMessageAsync(message, draft, ct);
            return true;
        }
        if(draft.CopyStage is "copy-text" or "copy-preview" || (draft.CopyStage=="channel-mode"&&draft.MessageText is not null))
        {await PreviewMarketingCopyAsync(message,draft,ct);return true;}
        if (!int.TryParse(NormalizeNumber(text ?? ""), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code <= 0)
        {
            await ReplyAsync(message.Chat.Id, "فقط کد عددی لیست را بفرستید؛ مثال: ۹۰۵۶. برای انصراف «لغو» را بفرستید.", ct);
            return true;
        }
        var list = await _db.SalesLists.AsNoTracking().FirstOrDefaultAsync(value => !value.IsDeleted &&
            !value.IsInventoryOffer && (value.StablePublicCode ?? value.PublicCode) == code && value.Status == SalesListStatus.Open &&
            value.TotalVolume > value.ReservedVolume && value.TelegramMessageId != null &&
            value.TelegramChannelId != null && value.TelegramPhotoFileId != null, ct);
        if (list is null || string.IsNullOrWhiteSpace(list.TelegramChannelId) || string.IsNullOrWhiteSpace(list.TelegramPhotoFileId))
        {
            await ReplyAsync(message.Chat.Id, "لیست باز و دارای عکس با این کد پیدا نشد؛ لیست تکمیل‌شده قابل تبلیغ نیست. کد دیگری بفرستید.", ct);
            return true;
        }
        await ShowMarketingCopyChoiceAsync(message,draft,code,list.Id,ct);
        return true;
    }

    private async Task QueueMarketingPromotionAsync(long chatId, long userId, long sourceMessageId,
        int code, Guid listId, string destination, string mode, CancellationToken ct, string? customText = null)
    {
        var id = MarketingPromotionJob.RequestId(chatId, userId, sourceMessageId);
        var now = DateTime.UtcNow;
        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var chat = chatId.ToString(CultureInfo.InvariantCulture);
            var eventType = destination == "group" ? "GroupPromotion" : "ChannelPromotion";
            var modeMarker = "\"ChannelMode\":\"" + mode + "\"";
            var candidates = await _db.NotificationOutbox.Where(value => value.Channel == MarketingPromotionJob.Channel &&
                !value.IsDeleted && (value.Id == id || (value.EventType == eventType && value.Recipient == chat &&
                value.Payload.Contains(listId.ToString()) &&
                (destination != "channel" || value.Payload.Contains(modeMarker) || (mode == "replace" && !value.Payload.Contains("\"ChannelMode\":"))) &&
                (value.Status == NotificationOutboxStatus.Pending ||
                value.Status == NotificationOutboxStatus.Processing ||
                (value.Status == NotificationOutboxStatus.Processed && value.CreatedAt > now.AddMinutes(-1)))))).ToArrayAsync(ct);
            // Compare bodies in memory: JSON-escaped Persian copy can exceed SQL
            // CHARINDEX/LIKE pattern limits even within Telegram's message limit.
            var duplicate = candidates.FirstOrDefault(value => value.Id == id ||
                JsonSerializer.Deserialize<MarketingPromotionJob>(value.Payload)?.CustomText == customText);
            if (duplicate is not null) id = duplicate.Id;
            else
            {
                _db.NotificationOutbox.Add(new NotificationOutbox
                {
                    Id = id, CustomerId = Guid.Empty, Channel = MarketingPromotionJob.Channel, EventType = eventType,
                    Recipient = chat, Payload = JsonSerializer.Serialize(new MarketingPromotionJob(listId, code, destination,
                        chatId, userId, ChannelMode: mode, CustomText: customText)), CreatedAt = now, Status = NotificationOutboxStatus.Pending
                });
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await SendMarketingStatusAsync(chatId, id, $"✅ درخواست تبلیغ لیست {code} در " +
            (destination == "group" ? "گروه" : "کانال (۲۴ ساعت)") + " ثبت شد.\nآماده‌سازی عکس و متن ممکن است چند دقیقه طول بکشد؛ نتیجه همین‌جا اعلام می‌شود.", ct);
    }
}
