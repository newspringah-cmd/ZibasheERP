using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed record FlashDraft(Guid CampaignId, string Step, DateTime ExpiresAt, Guid ListId = default,
        int Code = 0, int Minutes = 120, string Mode = "percent", int Percent = 0, decimal? Amount = null,
        decimal OriginalPrice = 0, string? Token = null, bool Inventory = false);
    private static readonly ConcurrentDictionary<(long ChatId, long UserId), FlashDraft> FlashDrafts = new();
    private const string FlashDenied = "این گزینه فقط برای مدیر @zahraa_frj فعال است.";
    private static string FlashAction(string action) => "invoiceadmin:flash:" + action;

    private Task FlashPromptAsync(long chatId, string text, CancellationToken ct) => _sender.SendInlineKeyboardAsync(
        chatId.ToString(CultureInfo.InvariantCulture), text,
        [[new TelegramInlineButton("↩️ انصراف", FlashAction("menu"))]], ct);

    private async Task HandleFlashCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chat = callback.Message!.Chat.Id; var key = (chat, callback.From.Id);
        if (!IsPrimaryOwner(callback.From.Id)) { await _sender.AnswerCallbackAsync(callback.Id, FlashDenied, ct, true); return; }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        var action = callback.Data!["invoiceadmin:flash:".Length..];
        if(action=="new")
        {
            ClearAdminWorkflowDrafts(chat,callback.From.Id);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"نوع پروموشن را انتخاب کنید:",
                [[new TelegramInlineButton("🧴 لیست عطر",FlashAction("new:list"))],[new TelegramInlineButton("📦 موجودی آماده",FlashAction("new:inventory"))]],ct);return;
        }
        if (action is "new:list" or "new:inventory")
        {
            ClearAdminWorkflowDrafts(chat, callback.From.Id);
            FlashDrafts[key] = new(Guid.NewGuid(), "code", DateTime.UtcNow.AddMinutes(20), Inventory: action=="new:inventory");
            await FlashPromptAsync(chat, action=="new:inventory"?"کد چهاررقمی موجودی آماده را وارد کنید.":"⏳ پروموشن ساعتی\n\nکد لیست عطر را بفرستید؛ مثال: ۴۵۵۹", ct); return;
        }
        if (action.StartsWith("mode:", StringComparison.Ordinal))
        {
            if (!FlashDrafts.TryGetValue(key, out var draft) || draft.Step != "mode" || draft.ExpiresAt <= DateTime.UtcNow || action[5..] is not ("percent" or "amount"))
            { await FlashPromptAsync(chat, "این انتخاب منقضی شده است؛ پروموشن جدید را دوباره شروع کنید.", ct); return; }
            FlashDrafts[key] = draft with { Step = "discount", Mode = action[5..] };
            await FlashPromptAsync(chat, action[5..] == "percent"
                ? "درصد تخفیف روی مبلغ هر میل را بفرستید؛ از ۱ تا ۹۹. مثال: ۲۰"
                : $"چند تومان از قیمت هر میل کم شود؟ فقط عدد بفرستید؛ مثال: ۱۰۰۰۰\nقیمت عادی هر میل: {HourlyPromotionCopy.Number(draft.OriginalPrice, "#,0.##")} تومان", ct); return;
        }
        if (action.StartsWith("start:", StringComparison.Ordinal))
        {
            if (!FlashDrafts.TryGetValue(key, out var draft) || draft.Step != "confirm" || draft.ExpiresAt <= DateTime.UtcNow || draft.Token != action[6..])
            { await FlashPromptAsync(chat, "این پیش‌نمایش منقضی یا جایگزین شده است؛ پروموشن را دوباره شروع کنید.", ct); return; }
            var list = await PromotionListAsync(draft.ListId, ct);
            if (list is null) { await FlashPromptAsync(chat, "لیست دیگر باز و آمادهٔ پروموشن نیست.", ct); return; }
            if (list.PricePerMl != draft.OriginalPrice)
            { await ShowFlashPreviewAsync(chat, callback.From.Id, draft with { OriginalPrice = list.PricePerMl }, list, ct); return; }
            var settings = new HourlyPromotionSettings(draft.CampaignId, draft.Minutes, draft.Percent, draft.Amount, draft.OriginalPrice);
            await QueueFlashAsync(draft.CampaignId, new MarketingPromotionJob(draft.ListId, draft.Code, MarketingPromotionJob.FlashStart,
                chat, callback.From.Id, FlashSale: settings), ct);
            ((ICollection<KeyValuePair<(long ChatId, long UserId), FlashDraft>>)FlashDrafts).Remove(new(key, draft));
            await SendFlashStatusAsync(chat, draft.CampaignId, "✅ شروع پروموشن تأیید شد؛ انتشار در گروه و کانال در حال انجام است. زمان از فعال‌شدن شروع می‌شود.", ct); return;
        }
        if (action.StartsWith("end:", StringComparison.Ordinal) && Guid.TryParseExact(action[4..], "N", out var endId))
        {
            var promotion = await _db.FlashSalePromotions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == endId && !value.IsDeleted, ct);
            if (promotion is null) { await FlashPromptAsync(chat, "پروموشن پیدا نشد.", ct); return; }
            await _sender.SendInlineKeyboardAsync(chat.ToString(), "پروموشن همین حالا پایان یابد؟ تخفیف آیتم‌های قبلی حفظ می‌شود و پیام تشکر در گروه و کانال ارسال می‌شود.",
                [[new TelegramInlineButton("⏹ بله، پایان پروموشن", FlashAction("end-confirm:" + endId.ToString("N")))],
                 [new TelegramInlineButton("↩️ انصراف", FlashAction("menu"))]], ct); return;
        }
        if (action.StartsWith("end-confirm:", StringComparison.Ordinal) && Guid.TryParseExact(action[12..], "N", out var campaignId))
        {
            if (await ZibasheERP.Infrastructure.Services.FlashSaleLifecycle.EndAsync(_db, campaignId, ct) is null)
            { await FlashPromptAsync(chat, "پروموشن پیدا نشد.", ct); return; }
            var jobId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("hourly-end:" + campaignId))[..16]);
            await QueueFlashAsync(jobId, new MarketingPromotionJob(Guid.Empty, 0, MarketingPromotionJob.FlashEnd,
                chat, callback.From.Id, FlashSale: new(campaignId)), ct);
            await SendFlashStatusAsync(chat, jobId, "✅ پروموشن همین حالا متوقف شد؛ تخفیف آیتم‌های قبلی محفوظ است. پیام تشکر در گروه و کانال ارسال می‌شود و نتیجه همین‌جا اعلام خواهد شد.", ct); return;
        }
        if (action.StartsWith("status:", StringComparison.Ordinal) && Guid.TryParseExact(action[7..], "N", out var statusId))
        {
            var job = await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(value => value.Id == statusId && value.Channel == MarketingPromotionJob.Channel, ct);
            var payload = job is null ? null : JsonSerializer.Deserialize<MarketingPromotionJob>(job.Payload);
            if (payload?.AdminUserId != callback.From.Id) { await FlashPromptAsync(chat, "درخواست پیدا نشد.", ct); return; }
            var status = job!.Status == NotificationOutboxStatus.Processed ? "✅ انجام شد." : job.Status == NotificationOutboxStatus.Failed
                ? "⚠️ " + job.LastError : "⏳ در صف یا در حال اجرا است.";
            await SendFlashStatusAsync(chat, statusId, status + (payload.ResultUrl is null ? "" : "\n" + payload.ResultUrl), ct); return;
        }
        ClearAdminWorkflowDrafts(chat, callback.From.Id);
        var active = await _db.FlashSalePromotions.AsNoTracking().Where(value => !value.IsDeleted && value.CancelledAtUtc == null && value.EndsAtUtc > DateTime.UtcNow)
            .OrderBy(value => value.EndsAtUtc).Take(10).Select(value => new { value.Id, value.EndsAtUtc, List = _db.SalesLists.Where(list => list.Id == value.SalesListId)
                .Select(list => list.InventoryCode ?? list.StablePublicCode ?? list.PublicCode).First() }).ToArrayAsync(ct);
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>> { new[] { new TelegramInlineButton("➕ پروموشن جدید", FlashAction("new")) } };
        foreach (var item in active) buttons.Add(new[] { new TelegramInlineButton($"⏹ پایان زودهنگام لیست {item.List}", FlashAction("end:" + item.Id.ToString("N"))) });
        buttons.Add(new[] { new TelegramInlineButton("↩️ منوی مدیریت", "invoiceadmin:menu:main") });
        await _sender.SendInlineKeyboardAsync(chat.ToString(), "⏳ پروموشن ساعتی\n\nکد لیست، مدت و میزان تخفیف را وارد کنید؛ بعد از پیش‌نمایش و تأیید، در گروه و کانال اجرا می‌شود.\n" +
            "پایان فرصت، تخفیف سفارش‌های جدید را متوقف می‌کند و پیام تشکر در هر دو مقصد می‌رود.\n" + (active.Length == 0 ? "پروموشن فعالی ندارید." : "برای پایان زودهنگام از دکمهٔ لیست استفاده کنید."), buttons, ct);
    }

    private Task<SalesList?> PromotionListAsync(Guid id, CancellationToken ct) => _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id &&
        !value.IsDeleted && (!value.IsInventoryOffer || value.InventoryCode != null) && value.Status == SalesListStatus.Open && value.TotalVolume > value.ReservedVolume && value.PricePerMl > 0 &&
        (value.IsInventoryOffer ? value.TelegramChannelId == _options.ReadyInventoryChannelId : value.TelegramChannelId == "-1001236168661") && value.TelegramMessageId != null && value.TelegramPhotoFileId != null, ct);

    private async Task<bool> TryHandleFlashMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !FlashDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft)) return false;
        var key = (message.Chat.Id, message.From.Id); var text = message.Text?.Trim() ?? "";
        if (!IsPrimaryOwner(message.From.Id)) { FlashDrafts.TryRemove(key, out _); await ReplyAsync(message.Chat.Id, FlashDenied, ct); return true; }
        if (draft.ExpiresAt <= DateTime.UtcNow) { FlashDrafts.TryRemove(key, out _); await FlashPromptAsync(message.Chat.Id, "زمان ورود اطلاعات تمام شد؛ پروموشن جدید را دوباره شروع کنید.", ct); return true; }
        if (text is "لغو" or "/cancel") { FlashDrafts.TryRemove(key, out _); await ReplyAsync(message.Chat.Id, "ساخت پروموشن لغو شد.", ct); return true; }
        if (text.StartsWith('/')) return false;
        var number = NormalizeNumber(text).Replace("٬", "").Replace(",", "").Replace('٫', '.');
        if (draft.Step == "code")
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code <= 0)
            { await FlashPromptAsync(message.Chat.Id, "فقط کد عددی لیست را بفرستید.", ct); return true; }
            var list = await _db.SalesLists.AsNoTracking().Where(value => (draft.Inventory ? value.InventoryCode == code && value.IsInventoryOffer : (value.StablePublicCode ?? value.PublicCode) == code && !value.IsInventoryOffer) && !value.IsDeleted && value.Status == SalesListStatus.Open)
                .OrderByDescending(value => value.OpenDate).FirstOrDefaultAsync(ct);
            if (list is null || await PromotionListAsync(list.Id, ct) is null)
            { await FlashPromptAsync(message.Chat.Id, "لیست باز و دارای عکس با این کد پیدا نشد.", ct); return true; }
            FlashDrafts[key] = draft with { Step = "hours", ListId = list.Id, Code = code, OriginalPrice = list.PricePerMl };
            await FlashPromptAsync(message.Chat.Id, "پروموشن چند ساعت فعال باشد؟ فقط عدد بفرستید؛ مثال: ۲ یا ۰٫۵\nحداکثر ۱۶۸ ساعت.", ct); return true;
        }
        if (draft.Step == "hours")
        {
            if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) || hours <= 0 || hours > 168 ||
                hours * 60 != decimal.Truncate(hours * 60) || hours * 60 < 1)
            { await FlashPromptAsync(message.Chat.Id, "مدت را به ساعت و با دقیقهٔ کامل وارد کنید؛ مثال: ۲ یا ۰٫۵. حداکثر ۱۶۸ ساعت.", ct); return true; }
            FlashDrafts[key] = draft with { Step = "mode", Minutes = (int)(hours * 60) };
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(), "تخفیف روی هر میل چگونه محاسبه شود؟",
                [[new TelegramInlineButton("٪ درصد تخفیف", FlashAction("mode:percent"))],
                 [new TelegramInlineButton("💰 مبلغ ثابت از هر میل", FlashAction("mode:amount"))],
                 [new TelegramInlineButton("↩️ انصراف", FlashAction("menu"))]], ct); return true;
        }
        if (draft.Step == "discount")
        {
            if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0 ||
                (draft.Mode == "percent" ? value >= 100 || value != decimal.Truncate(value) : value >= draft.OriginalPrice || value != decimal.Round(value, 2)))
            { await FlashPromptAsync(message.Chat.Id, draft.Mode == "percent" ? "درصد صحیح بین ۱ تا ۹۹ بفرستید." : "مبلغ مثبت و کمتر از قیمت هر میل بفرستید.", ct); return true; }
            var list = await PromotionListAsync(draft.ListId, ct);
            if (list is null) { await FlashPromptAsync(message.Chat.Id, "لیست دیگر آمادهٔ پروموشن نیست.", ct); return true; }
            await ShowFlashPreviewAsync(message.Chat.Id, message.From.Id, draft with { Percent = draft.Mode == "percent" ? (int)value : 0,
                Amount = draft.Mode == "amount" ? value : null, OriginalPrice = list.PricePerMl }, list, ct); return true;
        }
        await FlashPromptAsync(message.Chat.Id, "برای ادامه از دکمه‌های پیش‌نمایش استفاده کنید یا پروموشن جدید بسازید.", ct); return true;
    }

    private async Task ShowFlashPreviewAsync(long chat, long user, FlashDraft draft, SalesList list, CancellationToken ct)
    {
        var now = DateTime.UtcNow; var promotion = new FlashSalePromotion { StartsAtUtc = now, EndsAtUtc = now.AddMinutes(draft.Minutes),
            RequestedDurationMinutes = draft.Minutes, OriginalPricePerMl = list.PricePerMl, DiscountPercent = draft.Percent, DiscountAmountPerMl = draft.Amount };
        if (promotion.DiscountedPricePerMl <= 0 || promotion.DiscountedPricePerMl >= list.PricePerMl)
        { FlashDrafts[(chat, user)] = draft with { Step = "discount", Token = null }; await FlashPromptAsync(chat, "قیمت تخفیف‌دار باید مثبت و کمتر از قیمت عادی باشد؛ میزان تخفیف را اصلاح کنید.", ct); return; }
        var token = Guid.NewGuid().ToString("N")[..8]; FlashDrafts[(chat, user)] = draft with { Step = "confirm", Token = token, ExpiresAt = now.AddMinutes(20) };
        await ReplyAsync(chat, "👁 پیش‌نمایش پروموشن؛ پس از تأیید، همراه عکس و لینک لیست در گروه و کانال منتشر می‌شود. زمان هنوز شروع نشده است.", ct);
        await _sender.SendPhotoWithKeyboardAsync(chat.ToString(), list.TelegramPhotoFileId!, HourlyPromotionCopy.Caption(promotion, list, now),
            [[new TelegramInlineButton("✅ تأیید و شروع در گروه و کانال", FlashAction("start:" + token))],
             [new TelegramInlineButton("↩️ انصراف", FlashAction("menu"))]], ct);
    }

    private async Task QueueFlashAsync(Guid id, MarketingPromotionJob payload, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await _db.NotificationOutbox.AnyAsync(value => value.Id == id, ct))
        {
            _db.NotificationOutbox.Add(new NotificationOutbox { Id = id, CustomerId = Guid.Empty, Channel = MarketingPromotionJob.Channel,
                EventType = payload.Destination switch { MarketingPromotionJob.FlashStart => "HourlyPromotionStart", MarketingPromotionJob.FlashEnd => "HourlyPromotionEnd", "inventory-publish" => "ReadyInventoryPublication", "inventory-report" => "ReadyInventoryInvoiceReport", _ => "MarketingPromotion" },
                Recipient = payload.AdminChatId.ToString(CultureInfo.InvariantCulture), Payload = JsonSerializer.Serialize(payload), CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
    private Task SendFlashStatusAsync(long chat, Guid id, string text, CancellationToken ct) => _sender.SendInlineKeyboardAsync(chat.ToString(), text,
        [[new TelegramInlineButton("🔄 وضعیت درخواست", FlashAction("status:" + id.ToString("N")))],
         [new TelegramInlineButton("⏳ پروموشن‌ها", FlashAction("menu"))]], ct);
}
