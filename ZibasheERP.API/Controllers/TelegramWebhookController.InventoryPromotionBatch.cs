using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Inventory;
using ZibasheERP.API.Telegram;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const int InventoryPromotionPageSize = 50;
    private sealed record InventoryPromotionCard(Guid Id, int Code, decimal Total, string Photo);
    private sealed class InventoryPromotionDraft(InventoryPromotionCard[] cards)
    {
        public string Token { get; } = Guid.NewGuid().ToString("N")[..10];
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(30);
        public InventoryPromotionCard[] Cards { get; } = cards;
        public HashSet<Guid> Selected { get; } = [];
        public Dictionary<int, string> PagePhotos { get; } = [];
        public int Page { get; set; }
        public long? PageMessage { get; set; }
        public long? ReviewMessage { get; set; }
        public string Step { get; set; } = "browse";
        public int Percent { get; set; }
        public int Minutes { get; set; }
        public InventoryPromotionBatch.Selection[] Review { get; set; } = [];
        public int PageCount => (Cards.Length + InventoryPromotionPageSize - 1) / InventoryPromotionPageSize;
        public InventoryPromotionCard[] PageCards(int page) =>
            Cards.Skip(page * InventoryPromotionPageSize).Take(InventoryPromotionPageSize).ToArray();
    }

    private static readonly ConcurrentDictionary<(long Chat, long User), InventoryPromotionDraft> InventoryPromotionDrafts = new();
    private static readonly SemaphoreSlim InventoryPromotionGate = new(1, 1);

    private static string InventoryBatchAction(InventoryPromotionDraft draft, string action) =>
        FlashAction($"batch:{draft.Token}:{action}");

    private async Task<bool> TryHandleInventoryPromotionBatchCallbackAsync(TelegramCallbackQuery callback,
        string action, CancellationToken ct)
    {
        if (action != "batch" && !action.StartsWith("batch:", StringComparison.Ordinal)) return false;
        var chat = callback.Message!.Chat;
        if (chat.Id != callback.From.Id || chat.Type != "private" || !IsPrimaryOwner(callback.From.Id))
        {
            await ReplyAsync(chat.Id, "پروموشن دسته‌ای فقط در گفت‌وگوی خصوصی مدیر فعال است.", ct);
            return true;
        }
        var key = (chat.Id, callback.From.Id);
        await InventoryPromotionGate.WaitAsync(ct);
        try
        {
            var channel = _options.ReadyInventoryChannelId.Trim();
            if (string.IsNullOrWhiteSpace(channel)) throw new InvalidOperationException("کانال موجودی تنظیم نشده است.");
            if (action == "batch")
            {
                ClearAdminWorkflowDrafts(chat.Id, callback.From.Id);
                var cards = await new InventoryPromotionBatch(_db).Available(channel, DateTime.UtcNow)
                    .OrderByDescending(list => list.CreatedAt).ThenBy(list => list.InventoryCode)
                    .Select(list => new InventoryPromotionCard(list.Id, list.InventoryCode!.Value,
                        list.PricePerMl * list.TotalVolume + (list.FixedBottlePrice ?? 0), list.TelegramPhotoFileId!)).ToArrayAsync(ct);
                if (cards.Length == 0) throw new InvalidOperationException("موجودی منتشرشده و قابل پروموشن پیدا نشد.");
                var fresh = new InventoryPromotionDraft(cards);
                InventoryPromotionDrafts[key] = fresh;
                await ShowInventoryPromotionPageAsync(chat.Id, fresh, 0, ct);
                return true;
            }

            var parts = action.Split(':');
            if (parts.Length < 3 || !InventoryPromotionDrafts.TryGetValue(key, out var draft) ||
                parts[1] != draft.Token || draft.ExpiresAt <= DateTime.UtcNow)
                throw new InvalidOperationException("انتخاب منقضی شده است؛ پروموشن دسته‌ای را دوباره باز کنید.");
            draft.ExpiresAt = DateTime.UtcNow.AddMinutes(30);
            if (parts[2] == "apply" && draft.Step == "review" && draft.ReviewMessage == callback.Message.MessageId)
            {
                var ends = await new InventoryPromotionBatch(_db).ActivateAsync(draft.Review, channel, draft.Percent, draft.Minutes, callback.From.Id, ct);
                InventoryPromotionDrafts.TryRemove(key, out _);
                var tehran = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(ends, "Asia/Tehran");
                await FlashPromptAsync(chat.Id, $"✅ پروموشن {draft.Percent}٪ برای {draft.Review.Length} موجودی فعال شد.\n" +
                    $"پایان: {FormatShippingPersianDate(ends)} ساعت {tehran:HH:mm} به وقت ایران.\n" +
                    "قیمت روی پست‌های کانال به‌روز می‌شود و پیام تبلیغاتی با لینک سفارش در صف ارسال به گروه است.", ct);
                return true;
            }
            if (parts[2] == "browse")
            {
                await ShowInventoryPromotionPageAsync(chat.Id, draft, draft.Page, ct);
                return true;
            }
            if (draft.Step != "browse" || draft.PageMessage != callback.Message.MessageId)
                throw new InvalidOperationException("از دکمه‌های صفحهٔ فعلی استفاده کنید.");
            if (parts[2] == "next")
            {
                if (draft.Selected.Count == 0) throw new InvalidOperationException("حداقل یک موجودی را تیک بزنید.");
                draft.Step = "percent";
                await FlashPromptAsync(chat.Id, $"درصد تخفیف {draft.Selected.Count} موجودی را وارد کنید. مثال: ۲۰", ct);
                return true;
            }
            if (parts.Length == 4 && int.TryParse(parts[3], out var value))
            {
                if (parts[2] == "page" && value >= 0 && value < draft.PageCount)
                    await ShowInventoryPromotionPageAsync(chat.Id, draft, value, ct);
                else if (parts[2] == "pick" && draft.PageCards(draft.Page).FirstOrDefault(card => card.Code == value) is { } card)
                {
                    if (!draft.Selected.Add(card.Id)) draft.Selected.Remove(card.Id);
                    var edited = await _sender.EditReplyMarkupAsync(chat.Id.ToString(), callback.Message.MessageId,
                        InventoryPromotionPageButtons(draft, draft.Page), ct);
                    if (!edited.IsSuccessful && !IsTelegramMessageUnchanged(edited.Error))
                    {
                        if (!draft.Selected.Add(card.Id)) draft.Selected.Remove(card.Id);
                        throw new InvalidOperationException("علامت انتخاب به‌روز نشد؛ دوباره تیک بزنید.");
                    }
                }
            }
        }
        catch (InvalidOperationException error) { await ReplyAsync(chat.Id, error.Message, ct); }
        finally { InventoryPromotionGate.Release(); }
        return true;
    }

    private async Task ShowInventoryPromotionPageAsync(long chat, InventoryPromotionDraft draft, int page, CancellationToken ct)
    {
        var cards = draft.PageCards(page);
        var rows = InventoryPromotionPageButtons(draft, page);
        var caption = $"📦 پروموشن دسته‌ای | صفحهٔ {page + 1} از {draft.PageCount}\n" +
            "هر شماره روی عکس، دکمهٔ همان موجودی و قیمت کل آن به تومان است.";
        TelegramSendResult sent;
        if (draft.PagePhotos.TryGetValue(page, out var file))
            sent = await _sender.SendPhotoWithKeyboardAsync(chat.ToString(), file, caption, rows, ct);
        else
        {
            var photos = new byte[]?[cards.Length];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, cards.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = timeout.Token }, async (index, stop) =>
                    {
                        var photo = await _sender.DownloadFileAsync(cards[index].Photo, stop);
                        if (photo.IsSuccessful) photos[index] = photo.Content;
                    });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var sheet = InventoryPromotionPhotoPage.Render(cards.Select(card => card.Code).ToArray(), photos);
            sent = await _sender.SendPhotoBytesWithKeyboardAsync(chat.ToString(), sheet, "inventory-promotion.jpg", caption, rows, ct);
        }
        if (!sent.IsSuccessful || sent.MessageId is not >0)
            throw new InvalidOperationException("صفحهٔ عکس‌ها ارسال نشد؛ دوباره تلاش کنید.");
        if (sent.ExternalFileId is { Length: >0 } photoId) draft.PagePhotos[page] = photoId;
        var previous = draft.PageMessage;
        draft.Page = page;
        draft.PageMessage = sent.MessageId;
        draft.Step = "browse";
        if (previous.HasValue) await _sender.DeleteMessageAsync(chat.ToString(), previous.Value, ct);
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> InventoryPromotionPageButtons(InventoryPromotionDraft draft, int page)
    {
        var rows = draft.PageCards(page).Select((card, index) => new TelegramInlineButton(
            $"{(draft.Selected.Contains(card.Id) ? "✅" : "⬜")} {index + 1} · {card.Total:N0}",
            InventoryBatchAction(draft, $"pick:{card.Code}"))).Chunk(2).Cast<IReadOnlyCollection<TelegramInlineButton>>().ToList();
        var navigation = new List<TelegramInlineButton>();
        if (page > 0) navigation.Add(new("◀️ قبلی", InventoryBatchAction(draft, $"page:{page - 1}")));
        if (page + 1 < draft.PageCount) navigation.Add(new("بعدی ▶️", InventoryBatchAction(draft, $"page:{page + 1}")));
        if (navigation.Count > 0) rows.Add(navigation);
        rows.Add([new TelegramInlineButton($"ادامه با {draft.Selected.Count} انتخاب", InventoryBatchAction(draft, "next"))]);
        rows.Add([new TelegramInlineButton("❌ انصراف", FlashAction("menu"))]);
        return rows;
    }

    private async Task<bool> TryHandleInventoryPromotionBatchMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || message.Chat.Type != "private" || message.Chat.Id != message.From.Id || !IsPrimaryOwner(message.From.Id))
            return false;
        var chat = message.Chat.Id;
        var key = (chat, message.From.Id);
        if (!InventoryPromotionDrafts.ContainsKey(key)) return false;
        var input = message.Text?.Trim() ?? "";
        if (input.StartsWith('/') && input != "/cancel") return false;
        await InventoryPromotionGate.WaitAsync(ct);
        try
        {
            if (!InventoryPromotionDrafts.TryGetValue(key, out var draft)) return false;
            if (input is "/cancel" or "لغو" || draft.ExpiresAt <= DateTime.UtcNow)
            {
                InventoryPromotionDrafts.TryRemove(key, out _);
                await FlashPromptAsync(chat, "انتخاب پروموشن دسته‌ای بسته شد.", ct);
                return true;
            }
            var number = NormalizeNumber(input).Replace('٫', '.').TrimEnd('%', '٪');
            if (draft.Step == "percent")
            {
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is <1 or >99)
                    await ReplyAsync(chat, "درصد را از ۱ تا ۹۹ بفرستید.", ct);
                else
                {
                    draft.Percent = percent;
                    draft.Step = "hours";
                    await ReplyAsync(chat, "مدت پروموشن را به ساعت بفرستید. مثال: ۲۴ یا ۰٫۵", ct);
                }
                return true;
            }
            if (draft.Step == "hours")
            {
                if (number.EndsWith("ساعت", StringComparison.Ordinal)) number = number[..^4];
                if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) ||
                    hours <= 0 || hours > 168 || hours * 60 != decimal.Truncate(hours * 60))
                {
                    await ReplyAsync(chat, "مدت معتبر از یک دقیقه تا ۱۶۸ ساعت است. عدد را به ساعت وارد کنید.", ct);
                    return true;
                }
                draft.Minutes = (int)(hours * 60);
                await ShowInventoryPromotionReviewAsync(chat, draft, ct);
                return true;
            }
            return false;
        }
        catch (InvalidOperationException error)
        {
            await ReplyAsync(chat, error.Message, ct);
            return true;
        }
        finally { InventoryPromotionGate.Release(); }
    }

    private async Task ShowInventoryPromotionReviewAsync(long chat, InventoryPromotionDraft draft, CancellationToken ct)
    {
        var channel = _options.ReadyInventoryChannelId.Trim();
        var ids = draft.Selected.ToArray();
        var service = new InventoryPromotionBatch(_db);
        var linked = await service.Available(channel, DateTime.UtcNow).Where(list => ids.Contains(list.Id) && list.InventoryReferenceListId != null)
            .Select(list => list.Id).ToArrayAsync(ct);
        foreach (var id in linked) await InventoryReferencePricing.SynchronizeOneAsync(_db, id, ct);
        var lists = await service.Available(channel, DateTime.UtcNow).Where(list => ids.Contains(list.Id))
            .OrderBy(list => list.InventoryCode).ToArrayAsync(ct);
        if (lists.Length != ids.Length)
        {
            var available = lists.Select(list => list.Id).ToHashSet();
            var missing = draft.Cards.Where(card => draft.Selected.Contains(card.Id) && !available.Contains(card.Id)).Select(card => card.Code);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),
                "این کدها دیگر قابل پروموشن نیستند: " + string.Join("، ", missing) + "\nبه انتخاب‌ها برگردید و تیکشان را بردارید.",
                [[new TelegramInlineButton("↩️ انتخاب‌ها", InventoryBatchAction(draft, "browse"))]], ct);
            return;
        }
        var entries = lists.Select(InventoryPromotionBatch.FromList).ToArray();
        var text = $"👁 پیش‌نمایش · {entries.Length} موجودی · {draft.Percent}٪ · {draft.Minutes / 60m:0.##} ساعت\n" +
            "مبلغ اول قیمت عادی و مبلغ دوم قیمت پروموشن است. هزینهٔ شیشه ثابت است.\n\n" +
            string.Join("\n", entries.Select(item => $"• کد {item.Code} | {item.Total:N0} ← {item.DiscountedTotal(draft.Percent):N0} تومان"));
        foreach (var part in SplitTelegramMessage(text)) await ReplyAsync(chat, part, ct);
        var result = await _sender.SendInlineKeyboardAsync(chat.ToString(), "پروموشن روی همهٔ موجودی‌های بالا فعال شود؟",
            [[new TelegramInlineButton("✅ فعال‌کردن پروموشن", InventoryBatchAction(draft, "apply"))],
             [new TelegramInlineButton("↩️ انتخاب‌ها", InventoryBatchAction(draft, "browse"))],
             [new TelegramInlineButton("❌ انصراف", FlashAction("menu"))]], ct);
        if (!result.IsSuccessful || result.MessageId is not >0) throw new InvalidOperationException("پیش‌نمایش کامل ارسال نشد؛ مدت را دوباره وارد کنید.");
        draft.Review = entries;
        draft.ReviewMessage = result.MessageId;
        draft.Step = "review";
    }
}
