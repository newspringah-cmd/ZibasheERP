using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.PerfumeLabels;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed record PerfumeLogoDraft(Guid SalesListId, Guid PerfumeId);

    private static readonly ConcurrentDictionary<(long ChatId, long UserId), PerfumeLogoDraft>
        PerfumeLogoDrafts = new();

    private async Task<bool> TryHandlePerfumeLogoCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken ct)
    {
        if (callback.Message is null || string.IsNullOrWhiteSpace(callback.Data) ||
            !callback.Data.StartsWith("plogo:", StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(_options.LabelPrintChatId) &&
            !string.Equals(callback.Message.Chat.Id.ToString(), _options.LabelPrintChatId.Trim(), StringComparison.Ordinal))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این دکمه فقط در گروه چاپ لیبل فعال است.", ct, true);
            return true;
        }
        if (!await IsAuthorizedInvoiceAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", ct, true);
            return true;
        }

        var parts = callback.Data.Split(':');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "N", out var salesListId) ||
            parts[1] is not ("set" or "print"))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست نامعتبر است.", ct, true);
            return true;
        }

        var list = await LoadPerfumeLabelListAsync(salesListId, ct);
        if (list is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "لیست پیدا نشد.", ct, true);
            return true;
        }

        if (parts[1] == "set")
        {
            PerfumeLogoDrafts[(callback.Message.Chat.Id, callback.From.Id)] =
                new PerfumeLogoDraft(list.Id, list.PerfumeId);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                $"عکس لوگوی «{PerfumeLabelName(list)}» را به‌صورت Photo ارسال کنید.\n" +
                "لوگوی قبلی، در صورت وجود، جایگزین می‌شود.", ct);
            return true;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "در حال ساخت PDF…", ct);
        await CreateAndSendPerfumeLabelPdfAsync(callback.Message.Chat.Id, list, ct);
        return true;
    }

    private async Task<bool> TryHandlePerfumeLogoMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !PerfumeLogoDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            PerfumeLogoDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            return false;
        }
        if (string.Equals(message.Text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            PerfumeLogoDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "ثبت لوگو لغو شد.", ct);
            return true;
        }

        var photo = message.Photo?.OrderByDescending(value => value.FileSize ?? (long)value.Width * value.Height)
            .FirstOrDefault();
        if (photo is null)
        {
            await ReplyAsync(message.Chat.Id, "لوگو را به‌صورت Photo ارسال کنید.", ct);
            return true;
        }

        var perfume = await _db.Perfumes.FirstOrDefaultAsync(value =>
            value.Id == draft.PerfumeId && !value.IsDeleted, ct);
        if (perfume is null)
        {
            PerfumeLogoDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "عطر مربوط به این لوگو پیدا نشد.", ct);
            return true;
        }
        perfume.TelegramLogoFileId = photo.FileId;
        perfume.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        PerfumeLogoDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);

        await _sender.SendPhotoWithKeyboardAsync(message.Chat.Id.ToString(), photo.FileId,
            $"لوگوی «{(string.IsNullOrWhiteSpace(perfume.Name) ? perfume.EnglishName : perfume.Name)}» برای استفاده دائمی ثبت شد ✅",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("🖨 چاپ لوگو", $"plogo:print:{draft.SalesListId:N}") }
            }, ct);
        return true;
    }

    private async Task CreateAndSendPerfumeLabelPdfAsync(long chatId, SalesList list, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(list.Perfume.TelegramLogoFileId))
        {
            await ReplyAsync(chatId, "ابتدا با دکمه «ثبت لوگو» عکس لوگوی این عطر را ثبت کنید.", ct);
            return;
        }
        var download = await _sender.DownloadFileAsync(list.Perfume.TelegramLogoFileId, ct);
        if (!download.IsSuccessful || download.Content is null)
        {
            await ReplyAsync(chatId, $"⚠️ دریافت فایل لوگو ناموفق بود: {download.Error ?? "خطای نامشخص"}", ct);
            return;
        }

        var entries = BuildPerfumeLabelEntries(list);
        if (entries.Count == 0)
        {
            await ReplyAsync(chatId, "آیتمی برای چاپ لوگوی این لیست پیدا نشد.", ct);
            return;
        }
        try
        {
            var pdf = _perfumeLabelPdfService.Create(download.Content, entries);
            var result = await _sender.SendDocumentWithKeyboardAsync(
                chatId.ToString(), pdf, $"perfume-labels-{list.PublicCode}.pdf",
                $"🏷 PDF چاپ لوگو — لیست {list.PublicCode}\n{PerfumeLabelName(list)}\n" +
                $"تعداد لیبل: {entries.Count} | صفحه: {(entries.Count + 5) / 6}",
                Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
            if (!result.IsSuccessful)
                await ReplyAsync(chatId, $"⚠️ ارسال PDF ناموفق بود: {result.Error ?? "خطای نامشخص"}", ct);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Perfume label PDF generation failed for sales list {SalesListId}.", list.Id);
            await ReplyAsync(chatId, "⚠️ ساخت PDF لوگو با خطا روبه‌رو شد؛ جزئیات در لاگ ثبت شد.", ct);
        }
    }

    private async Task<SalesList?> LoadPerfumeLabelListAsync(Guid salesListId, CancellationToken ct) =>
        await _db.SalesLists.AsNoTracking()
            .Include(value => value.Perfume)
            .Include(value => value.Requests.Where(request => !request.IsDeleted &&
                request.Kind == SalesListRequestKind.CurrentBottle &&
                (request.Status == SalesListRequestStatus.Confirmed ||
                 request.Status == SalesListRequestStatus.Promoted ||
                 request.Status == SalesListRequestStatus.QueuedForInvoice ||
                 request.Status == SalesListRequestStatus.Invoiced)))
            .FirstOrDefaultAsync(value => value.Id == salesListId && !value.IsDeleted, ct);

    private static IReadOnlyCollection<PerfumeLabelEntry> BuildPerfumeLabelEntries(SalesList list)
    {
        var requests = list.Requests.OrderBy(value => value.ConfirmedAt ?? value.CreatedAt).ToArray();
        var owner = requests.FirstOrDefault(value => value.IsBottleOwner);
        var entries = new List<PerfumeLabelEntry>(requests.Length);
        foreach (var request in requests)
        {
            if (request.IsGift && owner is not null && IsPerfumeLabelGiftFor(request, owner))
                continue;
            // Bottle owner receives a logo label, but never an ml caption.
            entries.Add(new PerfumeLabelEntry(request.IsBottleOwner ? null : request.VolumeMl));
        }
        return entries;
    }

    private static bool IsPerfumeLabelGiftFor(SalesListRequest gift, SalesListRequest recipient)
    {
        if (!string.IsNullOrWhiteSpace(gift.GiftRecipientTelegramUserId) &&
            string.Equals(gift.GiftRecipientTelegramUserId.Trim(), recipient.TelegramUserId.Trim(),
                StringComparison.Ordinal))
            return true;
        var giftUsername = gift.GiftRecipientTelegramUsername?.Trim().TrimStart('@');
        var recipientUsername = recipient.TelegramUsername?.Trim().TrimStart('@');
        return !string.IsNullOrWhiteSpace(giftUsername) && !string.IsNullOrWhiteSpace(recipientUsername) &&
               string.Equals(giftUsername, recipientUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static string PerfumeLabelName(SalesList list) =>
        string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName;
}
