using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleTrackingImportCallbackAsync(
        TelegramCallbackQuery callback, CancellationToken ct)
    {
        try
        {
            return await TryHandleTrackingImportCallbackCoreAsync(callback, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tracking import callback failed at {CallbackData}.", callback.Data);
            if (callback.Message is not null)
                await ReplyAsync(callback.Message.Chat.Id,
                    "⚠️ عملیات کد رهگیری در این مرحله با خطای فنی متوقف شد؛ موارد ثبت‌شده حفظ شده‌اند و ارسال تکراری انجام نمی‌شود. جزئیات در لاگ ثبت شد.", ct);
            return callback.Data?.StartsWith("trackingimport:", StringComparison.Ordinal) == true;
        }
    }

    private async Task<bool> TryHandleTrackingImportCallbackCoreAsync(
        TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            !callback.Data.StartsWith("trackingimport:", StringComparison.Ordinal))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", ct, true);
            return true;
        }

        switch (callback.Data)
        {
            case "trackingimport:menu":
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                    "📬 ورود کدهای رهگیری\n\nنوع ورودی را انتخاب کنید. قبل از ارسال نهایی، پیش‌نمایش و نتیجه تطبیق نمایش داده می‌شود.",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton("📮 PDF پست", "trackingimport:post") },
                        new[] { new TelegramInlineButton("🚀 متن پست ویژه", "trackingimport:postexpress") },
                        new[] { new TelegramInlineButton("🚚 متن چاپار", "trackingimport:chapar") },
                        new[] { new TelegramInlineButton("↩️ بازگشت", "invoiceadmin:menu:main") }
                    }, ct);
                return true;
            case "trackingimport:post":
                _trackingImportDrafts.Set(callback.Message.Chat.Id, callback.From.Id, TrackingCarrier.IranPost);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callback.Message.Chat.Id,
                    "فایل PDF خروجی پست را ارسال کنید. جدول اصلی خوانده می‌شود و جدول بیمه نادیده گرفته خواهد شد. /cancel برای لغو", ct);
                return true;
            case "trackingimport:chapar":
                _trackingImportDrafts.Set(callback.Message.Chat.Id, callback.From.Id, TrackingCarrier.Chapar);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callback.Message.Chat.Id,
                    "متن کامل پیام‌های چاپار را یکجا ارسال کنید. /cancel برای لغو", ct);
                return true;
            case "trackingimport:postexpress":
                _trackingImportDrafts.Set(
                    callback.Message.Chat.Id, callback.From.Id, TrackingCarrier.IranPostExpress);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callback.Message.Chat.Id,
                    "متن کامل پیام‌های پست ویژه را یکجا ارسال کنید. خروجی با قالب قرمز پست ساخته می‌شود. /cancel برای لغو", ct);
                return true;
        }

        if (callback.Data.StartsWith("trackingimport:confirm:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["trackingimport:confirm:".Length..], "N", out var batchId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ارسال آغاز شد؛ موارد تکراری دوباره ارسال نمی‌شوند.", ct);
            try
            {
                await ConfirmTrackingBatchAsync(callback.Message.Chat.Id, batchId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Tracking batch confirmation failed for {BatchId}.", batchId);
                await ReplyAsync(callback.Message.Chat.Id,
                    "⚠️ مرحله ارسال نهایی با خطای فنی متوقف شد. نتیجه هر مقصد ثبت شده است؛ با زدن دوباره دکمه فقط مواردی که تلاش قبلی نداشته‌اند بررسی می‌شوند و ارسال تکراری انجام نمی‌شود.", ct);
            }
            return true;
        }
        if (callback.Data.StartsWith("trackingimport:cancel:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["trackingimport:cancel:".Length..], "N", out var cancelBatchId))
        {
            var batch = await _db.TrackingImportBatches.Include(value => value.Dispatches)
                .FirstOrDefaultAsync(value => value.Id == cancelBatchId, ct);
            if (batch is not null && batch.ConfirmedAt is null)
            {
                foreach (var dispatch in batch.Dispatches.Where(value => value.Status != TrackingDispatchStatus.Sent))
                {
                    dispatch.IsDeleted = true;
                    dispatch.UpdatedAt = DateTime.UtcNow;
                }
                batch.IsDeleted = true;
                batch.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            await _sender.AnswerCallbackAsync(callback.Id, "ارسال لغو شد.", ct, true);
            return true;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct, true);
        return true;
    }

    private async Task<bool> TryHandleTrackingImportMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        try
        {
            return await TryHandleTrackingImportMessageCoreAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tracking import message processing failed for message {MessageId}.",
                message.MessageId);
            if (message.From is not null &&
                _trackingImportDrafts.TryGet(message.Chat.Id, message.From.Id, out _))
            {
                await ReplyAsync(message.Chat.Id,
                    "⚠️ پردازش ورودی کد رهگیری با خطای فنی متوقف شد؛ هیچ ارسال نهایی انجام نشد. ورودی شما هنوز فعال است و پس از رفع مشکل می‌توانید دوباره فایل یا متن را بفرستید.", ct);
                return true;
            }
            return false;
        }
    }

    private async Task<bool> TryHandleTrackingImportMessageCoreAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_trackingImportDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _trackingImportDrafts.Remove(message.Chat.Id, message.From.Id);
            return true;
        }
        if (string.Equals(message.Text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _trackingImportDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ورود کد رهگیری لغو شد.", ct);
            return true;
        }

        // Telegram/proxies may close a webhook request while two independent vision reads are
        // still running. Continue safely after that disconnect, but stop on application shutdown
        // or after a bounded processing window.
        using var processingCts = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationLifetime.ApplicationStopping);
        processingCts.CancelAfter(TimeSpan.FromMinutes(8));
        ct = processingCts.Token;

        byte[] source;
        TrackingImportParseResult parsed;
        if (draft.Carrier == TrackingCarrier.IranPost)
        {
            if (message.Document is null ||
                !(string.Equals(message.Document.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                  message.Document.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true))
            {
                await ReplyAsync(message.Chat.Id, "لطفاً فایل PDF پست را به‌صورت Document ارسال کنید.", ct);
                return true;
            }
            if (message.Document.FileSize > 20 * 1024 * 1024)
            {
                await ReplyAsync(message.Chat.Id, "حجم PDF نباید بیشتر از ۲۰ مگابایت باشد.", ct);
                return true;
            }
            var download = await _sender.DownloadFileAsync(message.Document.FileId, ct);
            if (!download.IsSuccessful || download.Content is null)
            {
                await ReplyAsync(message.Chat.Id,
                    $"⚠️ مرحله دریافت فایل PDF از تلگرام ناموفق بود: {FriendlyTelegramError(download.Error)}\nهیچ پیامی ارسال نشد؛ فایل را دوباره بفرستید.", ct);
                return true;
            }
            source = download.Content;
            await ReplyAsync(message.Chat.Id, "PDF دریافت شد؛ در حال استخراج و تطبیق امن ردیف‌ها…", ct);
            parsed = await _trackingImportService.ParseIranPostPdfAsync(source, ct);
        }
        else
        {
            string? text = message.Text ?? message.Caption;
            if (string.IsNullOrWhiteSpace(text) && message.Document is not null &&
                (message.Document.FileName?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) == true ||
                 string.Equals(message.Document.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase)))
            {
                var download = await _sender.DownloadFileAsync(message.Document.FileId, ct);
                if (download.IsSuccessful && download.Content is not null)
                    text = Encoding.UTF8.GetString(download.Content);
                else
                {
                    var carrierTitle = draft.Carrier == TrackingCarrier.IranPostExpress
                        ? "پست ویژه"
                        : "چاپار";
                    await ReplyAsync(message.Chat.Id,
                        $"⚠️ مرحله دریافت فایل متنی {carrierTitle} از تلگرام ناموفق بود: {FriendlyTelegramError(download.Error)}\nهیچ پیامی ارسال نشد.", ct);
                    return true;
                }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                await ReplyAsync(message.Chat.Id,
                    draft.Carrier == TrackingCarrier.IranPostExpress
                        ? "متن کامل پیام پست ویژه را ارسال کنید."
                        : "متن کامل پیام چاپار را ارسال کنید.", ct);
                return true;
            }
            source = Encoding.UTF8.GetBytes(text);
            if (draft.Carrier == TrackingCarrier.IranPostExpress)
            {
                await ReplyAsync(message.Chat.Id,
                    "متن پست ویژه دریافت شد؛ در حال استخراج و تطبیق با درخواست‌ها و آدرس‌های اخیر…", ct);
                parsed = await _trackingImportService.ParseIranPostExpressAsync(text, ct);
            }
            else
            {
                await ReplyAsync(message.Chat.Id,
                    "متن چاپار دریافت شد؛ در حال تطبیق با درخواست‌ها و آدرس‌های اخیر…", ct);
                parsed = await _trackingImportService.ParseChaparAsync(text, ct);
            }
        }

        if (!parsed.IsSuccessful)
        {
            await ReplyAsync(message.Chat.Id, $"⚠️ {parsed.Error}", ct);
            return true;
        }

        _trackingImportDrafts.Remove(message.Chat.Id, message.From.Id);
        var sourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{(int)draft.Carrier}:").Concat(source).ToArray()));
        var existingBatch = await _db.TrackingImportBatches.AsNoTracking()
            .FirstOrDefaultAsync(value => value.SourceHash == sourceHash && !value.IsDeleted, ct);
        if (existingBatch is not null)
        {
            var reevaluated = await ReevaluateTrackingBatchMatchesAsync(existingBatch.Id, ct);
            await ReplyAsync(message.Chat.Id,
                reevaluated > 0
                    ? $"ℹ️ این ورودی قبلاً ثبت شده بود؛ بدون ثبت یا ارسال تکراری، تطبیق {reevaluated} مورد دوباره بررسی شد."
                    : "⚠️ این فایل/متن قبلاً وارد شده است؛ برای جلوگیری از ارسال تکراری دوباره پردازش نشد.", ct);
            await SendTrackingBatchPreviewAsync(message.Chat.Id, existingBatch.Id, false, ct);
            return true;
        }

        var batch = new TrackingImportBatch
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            SourceHash = sourceHash, Carrier = draft.Carrier,
            RequestedByTelegramUserId = message.From.Id,
            SourceChatId = message.Chat.Id, SourceMessageId = message.MessageId
        };
        var existingCodes = await _db.TrackingDispatches.AsNoTracking()
            .Where(value => !value.IsDeleted && value.Carrier == draft.Carrier &&
                parsed.Items.Select(item => item.TrackingCode).Contains(value.TrackingCode))
            .Select(value => value.TrackingCode).ToArrayAsync(ct);
        var duplicateCount = 0;
        try
        {
            foreach (var item in parsed.Items)
            {
                if (existingCodes.Contains(item.TrackingCode, StringComparer.Ordinal))
                {
                    duplicateCount++;
                    continue;
                }
                var match = await _trackingImportService.MatchAsync(item.RecipientName, item.Destination, ct);
                var status = item.IsSafeForAutomaticDelivery
                    ? match.Status
                    : TrackingDispatchStatus.NeedsReview;
                batch.Dispatches.Add(new TrackingDispatch
                {
                    Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                    Carrier = item.Carrier, TrackingCode = item.TrackingCode,
                    RecipientName = item.RecipientName, Destination = item.Destination,
                    TrackingUrl = item.TrackingUrl, CardImage = item.CardImage,
                    CustomerId = match.CustomerId,
                    ShippingRequestId = status == TrackingDispatchStatus.Ready ? match.ShippingRequestId : null,
                    Status = status,
                    MatchNotes = item.IsSafeForAutomaticDelivery
                        ? match.Notes
                        : $"{item.SafetyNote}؛ ارسال خودکار غیرفعال شد"
                });
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tracking customer matching failed for source {SourceHash}.", sourceHash);
            await ReplyAsync(message.Chat.Id,
                "⚠️ مرحله تطبیق نام گیرنده با درخواست‌ها و آدرس‌ها ناموفق بود؛ هیچ پیش‌نمایش یا پیامی برای مشتری ارسال نشد.", ct);
            return true;
        }

        foreach (var conflict in batch.Dispatches.Where(value => value.ShippingRequestId.HasValue)
                     .GroupBy(value => value.ShippingRequestId).Where(value => value.Count() > 1))
        {
            foreach (var dispatch in conflict)
            {
                dispatch.Status = TrackingDispatchStatus.NeedsReview;
                dispatch.MatchNotes = "بیش از یک کد به یک درخواست پست تطبیق داده شد";
                dispatch.ShippingRequestId = null;
            }
        }

        _db.TrackingImportBatches.Add(batch);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            var wasDuplicate = await _db.TrackingImportBatches.AsNoTracking()
                .AnyAsync(value => value.SourceHash == sourceHash && !value.IsDeleted, ct);
            if (wasDuplicate)
            {
                _logger.LogWarning(exception, "Concurrent duplicate tracking import was rejected.");
                await ReplyAsync(message.Chat.Id,
                    "⚠️ مرحله کنترل تکراری: همین ورودی هم‌زمان ثبت شده بود؛ پردازش دوم متوقف شد و ارسال تکراری انجام نشد.", ct);
            }
            else
            {
                _logger.LogError(exception, "Tracking preview persistence failed for source {SourceHash}.", sourceHash);
                await ReplyAsync(message.Chat.Id,
                    "⚠️ مرحله ذخیره پیش‌نمایش در دیتابیس ناموفق بود؛ هیچ پیامی برای مشتری ارسال نشد.", ct);
            }
            return true;
        }
        await SendTrackingBatchPreviewAsync(message.Chat.Id, batch.Id, true, ct, duplicateCount);
        return true;
    }

    private async Task SendTrackingBatchPreviewAsync(
        long chatId, Guid batchId, bool includeCards, CancellationToken ct, int duplicateCount = 0)
    {
        var batch = await _db.TrackingImportBatches.AsNoTracking()
            .Include(value => value.Dispatches).ThenInclude(value => value.Deliveries)
            .FirstAsync(value => value.Id == batchId, ct);
        var dispatches = batch.Dispatches.Where(value => !value.IsDeleted)
            .OrderBy(value => value.RecipientName).ToArray();
        if (includeCards)
        {
            foreach (var dispatch in dispatches)
            {
                var preview = await _sender.SendPhotoBytesWithKeyboardAsync(chatId.ToString(), dispatch.CardImage!,
                    $"tracking-{dispatch.TrackingCode}.png",
                    $"{(dispatch.Status == TrackingDispatchStatus.Ready ? "✅" : "⚠️")} {dispatch.RecipientName}\n" +
                    $"کد: {dispatch.TrackingCode}\n{dispatch.MatchNotes}", [], ct);
                if (!preview.IsSuccessful)
                {
                    _logger.LogWarning("Tracking preview delivery failed for {TrackingCode}: {Error}",
                        dispatch.TrackingCode, preview.Error);
                    await ReplyAsync(chatId,
                        $"⚠️ مرحله نمایش پیش‌نمایش برای کد {dispatch.TrackingCode} ناموفق بود: {FriendlyTelegramError(preview.Error)}\nهیچ ارسال نهایی انجام نشده است.", ct);
                    return;
                }
            }
        }
        var ready = dispatches.Count(value => value.Status == TrackingDispatchStatus.Ready);
        var failed = dispatches.Count(value => value.Status == TrackingDispatchStatus.Failed);
        var safelyRetryable = dispatches.Count(value => value.Status == TrackingDispatchStatus.Failed &&
            value.Deliveries.All(delivery => delivery.AttemptedAt is null));
        var review = dispatches.Count(value => value.Status == TrackingDispatchStatus.NeedsReview);
        var sent = dispatches.Count(value => value.Status == TrackingDispatchStatus.Sent);
        var lines = dispatches.Select((value, index) =>
            $"{index + 1}. {(value.Status == TrackingDispatchStatus.Ready ? "✅" : value.Status == TrackingDispatchStatus.Sent ? "☑️" : "⚠️")} " +
            $"{value.RecipientName} — {value.TrackingCode}");
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        if (ready + safelyRetryable > 0)
            buttons.Add(new[]
            {
                new TelegramInlineButton(
                    batch.ConfirmedAt is null ? $"✅ تأیید و ارسال {ready} مورد" : $"🔁 تلاش مجدد {safelyRetryable} مورد امن",
                    $"trackingimport:confirm:{batch.Id:N}")
            });
        if (batch.ConfirmedAt is null)
            buttons.Add(new[] { new TelegramInlineButton("❌ لغو این سری", $"trackingimport:cancel:{batch.Id:N}") });
        var summary = await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            $"📋 پیش‌نمایش کدهای رهگیری\n\nآماده ارسال: {ready}\nنیازمند بررسی: {review}\nناموفق: {failed} (تلاش مجدد امن: {safelyRetryable})\nارسال‌شده قبلی: {sent}\n" +
            $"تکراری حذف‌شده: {duplicateCount}\n\n{string.Join("\n", lines)}\n\n" +
            "فقط موارد دارای تیک سبز ارسال می‌شوند؛ موارد مبهم خودکار ارسال نخواهند شد.", buttons, ct);
        if (!summary.IsSuccessful)
        {
            _logger.LogWarning("Tracking preview summary failed for batch {BatchId}: {Error}", batchId, summary.Error);
            await ReplyAsync(chatId,
                $"⚠️ مرحله نمایش خلاصه و دکمه تأیید ناموفق بود: {FriendlyTelegramError(summary.Error)}\nهیچ ارسال نهایی انجام نشده است.", ct);
        }
    }

    private async Task<int> ReevaluateTrackingBatchMatchesAsync(Guid batchId, CancellationToken ct)
    {
        var dispatches = await _db.TrackingDispatches
            .Where(value => value.ImportBatchId == batchId && !value.IsDeleted &&
                value.Status == TrackingDispatchStatus.NeedsReview && value.SentAt == null)
            .ToArrayAsync(ct);
        var updated = 0;
        foreach (var dispatch in dispatches)
        {
            // Low-confidence PDF rows stay blocked even if a customer name happens to match.
            if (dispatch.MatchNotes?.Contains("اطمینان خواندن ردیف پایین", StringComparison.Ordinal) == true ||
                dispatch.MatchNotes?.Contains("استخراج متن پست ویژه نیازمند بررسی", StringComparison.Ordinal) == true)
                continue;
            var match = await _trackingImportService.MatchAsync(
                dispatch.RecipientName, dispatch.Destination, ct);
            if (match.Status != TrackingDispatchStatus.Ready || !match.CustomerId.HasValue)
                continue;
            dispatch.CustomerId = match.CustomerId;
            dispatch.ShippingRequestId = match.ShippingRequestId;
            dispatch.Status = TrackingDispatchStatus.Ready;
            dispatch.MatchNotes = $"{match.Notes} — تطبیق مجدد";
            dispatch.UpdatedAt = DateTime.UtcNow;
            updated++;
        }
        if (updated > 0) await _db.SaveChangesAsync(ct);
        return updated;
    }

    private async Task ConfirmTrackingBatchAsync(long adminChatId, Guid batchId, CancellationToken ct)
    {
        var batch = await _db.TrackingImportBatches.FirstOrDefaultAsync(value => value.Id == batchId && !value.IsDeleted, ct);
        if (batch is null)
        {
            await ReplyAsync(adminChatId, "سری کد رهگیری پیدا نشد.", ct);
            return;
        }
        batch.ConfirmedAt ??= DateTime.UtcNow;
        batch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ids = await _db.TrackingDispatches.AsNoTracking()
            .Where(value => value.ImportBatchId == batchId && !value.IsDeleted &&
                (value.Status == TrackingDispatchStatus.Ready || value.Status == TrackingDispatchStatus.Failed))
            .Select(value => value.Id).ToArrayAsync(ct);
        var sentCount = 0;
        var failedCount = 0;
        var failureDetails = new List<string>();
        foreach (var id in ids)
        {
            var claimed = await _db.TrackingDispatches
                .Where(value => value.Id == id &&
                    (value.Status == TrackingDispatchStatus.Ready || value.Status == TrackingDispatchStatus.Failed))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Status, TrackingDispatchStatus.Sending)
                    .SetProperty(value => value.SendingStartedAt, DateTime.UtcNow)
                    .SetProperty(value => value.UpdatedAt, DateTime.UtcNow), ct);
            if (claimed == 0) continue;
            _db.ChangeTracker.Clear();
            var dispatch = await _db.TrackingDispatches.Include(value => value.Deliveries)
                .FirstAsync(value => value.Id == id, ct);
            if (!dispatch.CustomerId.HasValue || dispatch.CardImage is null)
            {
                dispatch.Status = TrackingDispatchStatus.Failed;
                dispatch.LastError = "اطلاعات مشتری یا تصویر کارت ناقص است";
                await _db.SaveChangesAsync(ct);
                failedCount++;
                failureDetails.Add($"{dispatch.RecipientName}: مرحله آماده‌سازی ارسال — اطلاعات مشتری یا تصویر کارت ناقص است");
                continue;
            }

            var chatIds = await _db.CustomerTelegramGroups.AsNoTracking()
                .Where(value => !value.IsDeleted && value.IsActive && value.CustomerId == dispatch.CustomerId)
                .OrderByDescending(value => value.LastSeenAt ?? value.LinkedAt)
                .Select(value => value.ChatId).Distinct().ToArrayAsync(ct);
            if (chatIds.Length == 0)
            {
                dispatch.Status = TrackingDispatchStatus.Failed;
                dispatch.LastError = "گروه فعال مشتری پیدا نشد";
                await _db.SaveChangesAsync(ct);
                failedCount++;
                failureDetails.Add($"{dispatch.RecipientName}: مرحله یافتن مقصد — گروه فعال مشتری پیدا نشد");
                continue;
            }
            foreach (var chatId in chatIds)
            {
                var delivery = dispatch.Deliveries.FirstOrDefault(value => value.TelegramChatId == chatId);
                if (delivery?.SentAt is not null) continue;
                if (delivery?.AttemptedAt is not null)
                {
                    delivery.LastError = "نتیجه تلاش قبلی نامطمئن است؛ برای جلوگیری از ارسال تکراری خودکار تکرار نشد";
                    continue;
                }
                delivery ??= new TrackingDispatchDelivery
                {
                    Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                    TrackingDispatchId = dispatch.Id, TelegramChatId = chatId
                };
                if (delivery.TrackingDispatch is null && !dispatch.Deliveries.Contains(delivery))
                    dispatch.Deliveries.Add(delivery);
                delivery.AttemptedAt = DateTime.UtcNow;
                delivery.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                var result = await _sender.SendPhotoBytesWithKeyboardAsync(chatId, dispatch.CardImage,
                    $"tracking-{dispatch.TrackingCode}.png",
                    $"📦 کد رهگیری مرسوله شما\n{dispatch.TrackingCode}" +
                    (string.IsNullOrWhiteSpace(dispatch.TrackingUrl) ? "" : $"\n{dispatch.TrackingUrl}"), [], ct);
                if (result.IsSuccessful)
                {
                    delivery.SentAt = DateTime.UtcNow;
                    delivery.LastError = null;
                }
                else
                    delivery.LastError = result.Error;
                delivery.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            if (dispatch.Deliveries.Count(value => value.SentAt.HasValue) == chatIds.Length)
            {
                dispatch.Status = TrackingDispatchStatus.Sent;
                dispatch.SentAt = DateTime.UtcNow;
                dispatch.LastError = null;
                await MarkShippingRequestSentAsync(dispatch, ct);
                sentCount++;
            }
            else
            {
                dispatch.Status = TrackingDispatchStatus.Failed;
                dispatch.LastError = "ارسال به همه گروه‌های فعال مشتری کامل نشد";
                failedCount++;
                var deliveryErrors = dispatch.Deliveries
                    .Where(value => !value.SentAt.HasValue)
                    .Select(value => value.LastError)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct()
                    .ToArray();
                failureDetails.Add($"{dispatch.RecipientName}: مرحله ارسال تلگرام — " +
                    (deliveryErrors.Length == 0 ? dispatch.LastError : string.Join("؛ ", deliveryErrors)));
            }
            dispatch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        await ReplyAsync(adminChatId,
            $"نتیجه ارسال کد رهگیری:\n✅ موفق: {sentCount}\n⚠️ ناموفق: {failedCount}\n" +
            "موارد مبهم و تکراری ارسال نشدند." +
            (failureDetails.Count == 0 ? "" : $"\n\nجزئیات خطا:\n{string.Join("\n", failureDetails.Take(15).Select(value => $"• {value}"))}"), ct);
    }

    private async Task MarkShippingRequestSentAsync(TrackingDispatch dispatch, CancellationToken ct)
    {
        if (!dispatch.ShippingRequestId.HasValue) return;
        var items = await _db.OrderItems.Include(value => value.Order).ThenInclude(value => value!.DeliveryAddress)
            .Where(value => !value.IsDeleted && value.ShippingRequestId == dispatch.ShippingRequestId)
            .ToArrayAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.FulfillmentStatus = OrderItemFulfillmentStatus.Shipped;
            item.ShippedAt = now;
            item.UpdatedAt = now;
        }
        foreach (var order in items.Select(value => value.Order).Where(value => value is not null).Distinct()!)
        {
            var hasUnshipped = await _db.OrderItems.AnyAsync(value => !value.IsDeleted && value.OrderId == order!.Id &&
                value.ShippingRequestId != dispatch.ShippingRequestId &&
                value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped, ct);
            if (!hasUnshipped)
            {
                order!.Status = OrderStatus.Shipped;
                order.ShippedAt = now;
                order.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);

        var messageId = items.Select(value => value.ShippingTelegramMessageId).FirstOrDefault(value => value.HasValue);
        if (messageId.HasValue && !string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ ارسال شد", "shipping:trackingdone"),
                    new TelegramInlineButton("✅ کد ارسال شد", "shipping:trackingdone")
                }
            };
            var result = await _sender.EditReplyMarkupAsync(
                _options.ShippingChatId.Trim(), messageId.Value, buttons, ct);
            if (!result.IsSuccessful && !IsTelegramMessageUnchanged(result.Error))
                _logger.LogWarning("Tracking source message update failed: {Error}", result.Error);
        }
    }

    private static string FriendlyTelegramError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "پاسخی از تلگرام دریافت نشد";
        if (error.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            return "گروه مقصد پیدا نشد یا ربات دیگر عضو گروه نیست";
        if (error.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("kicked", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not enough rights", StringComparison.OrdinalIgnoreCase))
            return "ربات در گروه مقصد دسترسی ارسال ندارد";
        if (error.Contains("file is too big", StringComparison.OrdinalIgnoreCase))
            return "حجم فایل بیشتر از حد مجاز تلگرام است";
        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("temporarily", StringComparison.OrdinalIgnoreCase))
            return "ارتباط با تلگرام موقتاً قطع یا کند شده است";
        if (error.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            return "وضعیت پیام از قبل به‌روز بوده است";
        return "تلگرام عملیات را نپذیرفت؛ جزئیات فنی در لاگ ثبت شد";
    }
}
