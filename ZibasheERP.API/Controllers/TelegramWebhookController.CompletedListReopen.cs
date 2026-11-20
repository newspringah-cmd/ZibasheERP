using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string CompletedListReopenPrefix = "invoiceadmin:clr:";

    private static string CompletedListBottleName(SalesListRequest request) =>
        request.IsBottleOwner ? "صاحب باتل" :
        request.IsComplimentaryBottle ? "شیشه رایگان" : request.Bottle?.Name ?? "شیشه نامشخص";

    private static TelegramInlineButton[][] CompletedListReopenButtons(Guid token) =>
        [[new("↩️ برگشت لیست به کانال", $"{CompletedListReopenPrefix}{token:N}")]];

    private async Task HandleCompletedListReopenAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var userId = callback.From.Id;
        if (!Guid.TryParseExact(callback.Data![CompletedListReopenPrefix.Length..], "N", out var token) ||
            !CompletedListEditDrafts.TryGetValue((chatId, userId), out var draft) ||
            draft.PendingReopen is not { } pending ||
            pending.Token != token || draft.UpdatedAt <= DateTime.UtcNow.AddMinutes(-20))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این گزینه منقضی شده است؛ دوباره وارد ویرایش شوید.", ct);
            return;
        }
        if (!await draft.ReopenGate.WaitAsync(0, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "بازگشایی در حال انجام است.", ct);
            return;
        }
        try
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (draft.Reopened)
            {
                await PublishReopenedCompletedListAsync(chatId, userId, draft, ct);
                return;
            }
            await TryHandleCompletedListEditMessageAsync(
                callback.Message with { From = callback.From, Text = pending.Text }, ct, draft);
        }
        catch (Exception error) when (error is DbUpdateException or System.Data.Common.DbException)
        {
            await ReplyAsync(chatId, "بازگشایی انجام نشد؛ وضعیت لیست را دوباره بررسی کنید.", ct);
        }
        finally
        {
            draft.ReopenGate.Release();
        }
    }

    private async Task<(SalesList? Successor, string? Error)> FindCompletedListReopenPostAsync(
        SalesList list, CancellationToken ct)
    {
        if (list.Status is not (SalesListStatus.Closed or SalesListStatus.Full) || list.IsInventoryOffer ||
            list.BatchId.HasValue ||
            await _db.InvoiceIssuanceBatchSalesLists.AnyAsync(link => link.SalesListId == list.Id, ct) ||
            await _db.OrderItems.AnyAsync(item => item.SalesListId == list.Id && !item.IsDeleted, ct))
            return (null, "این لیست وارد روال خرید یا فاکتور شده و قابل بازگشایی نیست.");

        var code = list.DisplayCode;
        var laterCycles = await _db.SalesLists
            .Where(candidate => !candidate.IsDeleted && candidate.Id != list.Id &&
                candidate.PerfumeId == list.PerfumeId && candidate.OpenDate > list.OpenDate &&
                candidate.Status != SalesListStatus.Cancelled &&
                (candidate.StablePublicCode ?? candidate.PublicCode) == code)
            .Take(2).ToArrayAsync(ct);
        if (laterCycles.Length > 1)
            return (null, "بعد از این لیست چند باتل دیگر ثبت شده؛ بازگشایی خودکار انجام نمی‌شود.");

        var successor = laterCycles.SingleOrDefault();
        if (successor is not null &&
            (successor.Status != SalesListStatus.Open || successor.ReservedVolume != 0 ||
             successor.HasBottleOwner || successor.BatchId.HasValue ||
             await _db.InvoiceIssuanceBatchSalesLists.AnyAsync(link => link.SalesListId == successor.Id, ct) ||
             await _db.OrderItems.AnyAsync(item => item.SalesListId == successor.Id && !item.IsDeleted, ct) ||
             await _db.SalesListRequests.AnyAsync(request => request.SalesListId == successor.Id &&
                 !request.IsDeleted && request.Status != SalesListRequestStatus.Cancelled &&
                 request.Status != SalesListRequestStatus.Expired, ct)))
            return (null, "باتل بعدی سفارش یا فرایند فعال دارد؛ بازگشایی خودکار انجام نمی‌شود.");

        var post = successor ?? list;
        if (!post.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(_options.SalesChannelId) ||
            post.TelegramChannelId != _options.SalesChannelId)
            return (null, "پست اصلی این لیست در کانال فروش مشخص نیست؛ بازگشایی خودکار انجام نمی‌شود.");

        if (successor is not null && list.TelegramMessageId.HasValue &&
            list.TelegramMessageId != successor.TelegramMessageId)
            return (null, "برای این لیست دو پست متفاوت ثبت شده؛ بازگشایی نیاز به بررسی دارد.");

        var now = DateTime.UtcNow;
        if (await _db.SalesListRequests.AnyAsync(request => request.SalesListId == list.Id &&
                !request.IsDeleted && request.Status == SalesListRequestStatus.PendingConfirmation &&
                request.ExpiresAt > now, ct))
            return (null, "ثبت موقت در این لیست فعال است؛ بعد از پایان مهلت آن دوباره امتحان کنید.");

        return (successor, null);
    }

    private async Task PublishReopenedCompletedListAsync(long chatId, long userId, CompletedListEditDraft draft,
        CancellationToken ct)
    {
        try
        {
            await RefreshChannelSalesListAsync(draft.SalesListId, ct, sendRemainingAlerts: false);
        }
        catch (InvalidOperationException) when (!ct.IsCancellationRequested && draft.PendingReopen is not null)
        {
            draft.UpdatedAt = DateTime.UtcNow;
            await _sender.SendInlineKeyboardAsync(chatId.ToString(),
                "حذف و بازگشایی ثبت شد. به‌روزرسانی پست کانال ناموفق بود؛ برای تلاش مجدد همین دکمه را بزنید.",
                CompletedListReopenButtons(draft.PendingReopen.Value.Token), ct);
            return;
        }

        CompletedListEditDrafts.TryRemove(new KeyValuePair<(long, long), CompletedListEditDraft>((chatId, userId), draft));
        await ReplyAsync(chatId, $"لیست {draft.PublicCode} باز شد و پست کانال فروش به‌روزرسانی شد ✅", ct);
    }
}
