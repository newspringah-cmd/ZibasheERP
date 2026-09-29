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
            case "trackingimport:noop":
                await _sender.AnswerCallbackAsync(callback.Id, "این کد قبلاً ارسال شده است.", ct);
                return true;
            case "trackingimport:menu":
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                    "📬 ورود کدهای رهگیری\n\nنوع ورودی را انتخاب کنید. قبل از ارسال نهایی، پیش‌نمایش و نتیجه تطبیق نمایش داده می‌شود.",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton("📮 PDF پست", "trackingimport:post") },
                        new[] { new TelegramInlineButton("🚀 متن پست ویژه", "trackingimport:postexpress") },
                        new[] { new TelegramInlineButton("🚚 متن چاپار", "trackingimport:chapar") },
                        new[] { new TelegramInlineButton("📊 گزارش ارسال سری آخر", "trackingimport:lastreport") },
                        new[] { new TelegramInlineButton("↩️ بازگشت", "invoiceadmin:menu:main") }
                    }, ct);
                return true;
            case "trackingimport:lastreport":
                await _sender.AnswerCallbackAsync(callback.Id, "در حال تهیه گزارش سری آخر…", ct);
                await SendLatestTrackingBatchReportAsync(callback.Message.Chat.Id, ct);
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

        if (TryParseTrackingDispatchCallback(callback.Data, "send", out var sendDispatchId))
        {
            await _sender.AnswerCallbackAsync(callback.Id,
                "ارسال این کد آغاز شد؛ ارسال تکراری انجام نمی‌شود.", ct);
            await ConfirmTrackingDispatchAsync(
                callback.Message.Chat.Id, callback.Message.MessageId, sendDispatchId, ct);
            return true;
        }
        if (TryParseTrackingDispatchCallback(callback.Data, "rebuild", out var rebuildDispatchId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "در حال بازبینی تطبیق و ساخت مجدد…", ct);
            await RebuildTrackingDispatchAsync(
                callback.Message.Chat.Id, callback.Message.MessageId, rebuildDispatchId, ct);
            return true;
        }
        if (TryParseTrackingDispatchCallback(callback.Data, "delete", out var deleteDispatchId))
        {
            await DeleteTrackingDispatchAsync(
                callback.Id, callback.Message.Chat.Id, callback.Message.MessageId, deleteDispatchId, ct);
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

    private async Task SendLatestTrackingBatchReportAsync(long chatId, CancellationToken ct)
    {
        var batch = await _db.TrackingImportBatches.AsNoTracking()
            .Where(value => !value.IsDeleted)
            .OrderByDescending(value => value.CreatedAt)
            .Include(value => value.Dispatches)
            .ThenInclude(value => value.Deliveries)
            .FirstOrDefaultAsync(ct);
        if (batch is null)
        {
            await ReplyAsync(chatId, "هنوز هیچ سری کد رهگیری ثبت نشده است.", ct);
            return;
        }

        var active = batch.Dispatches.Where(value => !value.IsDeleted)
            .OrderBy(value => value.RecipientName)
            .ThenBy(value => value.TrackingCode)
            .ToArray();
        var deletedCount = batch.Dispatches.Count(value => value.IsDeleted);
        var sent = active.Count(value => value.Status == TrackingDispatchStatus.Sent);
        var ready = active.Count(value => value.Status == TrackingDispatchStatus.Ready);
        var review = active.Count(value => value.Status == TrackingDispatchStatus.NeedsReview);
        var sending = active.Count(value => value.Status == TrackingDispatchStatus.Sending);
        var failed = active.Count(value => value.Status == TrackingDispatchStatus.Failed);
        var allSent = active.Length > 0 && sent == active.Length;
        var carrier = batch.Carrier switch
        {
            TrackingCarrier.IranPost => "پست",
            TrackingCarrier.IranPostExpress => "پست ویژه",
            TrackingCarrier.Chapar => "چاپار",
            _ => "نامشخص"
        };

        var lines = new List<string>
        {
            "📊 گزارش ارسال سری آخر کدهای رهگیری",
            "",
            $"شرکت ارسال: {carrier}",
            $"زمان ثبت: {batch.CreatedAt.AddHours(3.5):yyyy/MM/dd HH:mm}",
            $"نتیجه کلی: {(allSent ? "✅ همه کدها ارسال شده‌اند" : "⚠️ همه کدها ارسال نشده‌اند")}",
            "",
            $"کل فعال: {active.Length}",
            $"✅ ارسال‌شده: {sent}",
            $"⏳ آماده ارسال: {ready}",
            $"⚠️ نیازمند بررسی: {review}",
            $"🔄 در حال ارسال: {sending}",
            $"❌ ناموفق: {failed}",
            $"🗑 حذف‌شده: {deletedCount}",
            "",
            "جزئیات:"
        };
        foreach (var dispatch in active)
        {
            var successfulDestinations = dispatch.Deliveries.Count(value => value.SentAt.HasValue);
            var knownDestinations = dispatch.Deliveries.Count;
            var state = dispatch.Status switch
            {
                TrackingDispatchStatus.Sent => "✅ رسیده",
                TrackingDispatchStatus.Ready => "⏳ ارسال‌نشده",
                TrackingDispatchStatus.NeedsReview => "⚠️ نیازمند بررسی",
                TrackingDispatchStatus.Sending => "🔄 در حال ارسال",
                TrackingDispatchStatus.Failed => "❌ ناموفق",
                _ => "❔ نامشخص"
            };
            var deliveryText = knownDestinations > 0
                ? $" | مقصد موفق: {successfulDestinations}/{knownDestinations}"
                : "";
            var errorText = dispatch.Status == TrackingDispatchStatus.Failed &&
                            !string.IsNullOrWhiteSpace(dispatch.LastError)
                ? $" | {dispatch.LastError}"
                : "";
            lines.Add($"{state} | {dispatch.RecipientName} | {dispatch.TrackingCode}{deliveryText}{errorText}");
        }

        var parts = SplitTelegramMessage(string.Join("\n", lines)).ToArray();
        for (var index = 0; index < parts.Length; index++)
        {
            TelegramSendResult result;
            if (index == parts.Length - 1)
            {
                result = await _sender.SendInlineKeyboardAsync(chatId.ToString(), parts[index],
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton("🔄 بروزرسانی گزارش", "trackingimport:lastreport") },
                        new[] { new TelegramInlineButton("↩️ بازگشت", "trackingimport:menu") }
                    }, ct);
            }
            else
            {
                result = await _sender.SendAsync(chatId.ToString(), parts[index], ct);
            }

            if (!result.IsSuccessful)
            {
                _logger.LogWarning("Latest tracking batch report delivery failed for batch {BatchId}: {Error}",
                    batch.Id, result.Error);
                await ReplyAsync(chatId,
                    $"⚠️ ارسال گزارش سری آخر کامل نشد: {FriendlyTelegramError(result.Error)}", ct);
                return;
            }
        }
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
            var hasDeliveryAttempt = await _db.TrackingDispatches.AsNoTracking()
                .Where(value => value.ImportBatchId == existingBatch.Id)
                .AnyAsync(value => value.Status == TrackingDispatchStatus.Sent ||
                    value.Status == TrackingDispatchStatus.Sending ||
                    value.Deliveries.Any(delivery => delivery.AttemptedAt.HasValue), ct);
            if (hasDeliveryAttempt)
            {
                var reevaluated = await ReevaluateTrackingBatchMatchesAsync(existingBatch.Id, ct);
                await ReplyAsync(message.Chat.Id,
                    reevaluated > 0
                        ? $"ℹ️ این ورودی قبلاً ثبت شده بود؛ بدون ثبت یا ارسال تکراری، تطبیق {reevaluated} مورد دوباره بررسی شد."
                        : "⚠️ این فایل/متن قبلاً وارد شده و حداقل یک تلاش ارسال دارد؛ برای جلوگیری از ارسال تکراری دوباره پردازش نشد.", ct);
                await SendTrackingBatchPreviewAsync(message.Chat.Id, existingBatch.Id, false, ct);
                return true;
            }

            var now = DateTime.UtcNow;
            await _db.TrackingDispatches
                .Where(value => value.ImportBatchId == existingBatch.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.IsDeleted, true)
                    .SetProperty(value => value.ShippingRequestId, (Guid?)null)
                    .SetProperty(value => value.UpdatedAt, now), ct);
            await _db.TrackingImportBatches
                .Where(value => value.Id == existingBatch.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.IsDeleted, true)
                    .SetProperty(value => value.UpdatedAt, now), ct);
            await ReplyAsync(message.Chat.Id,
                "ℹ️ نسخه قبلی این فایل که هنوز ارسال نشده بود کنار گذاشته شد؛ پیش‌نمایش‌ها با برش قطعی PDF دوباره ساخته می‌شوند.", ct);
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

        var candidateShippingRequestIds = batch.Dispatches
            .Where(value => value.Status == TrackingDispatchStatus.Ready && value.ShippingRequestId.HasValue)
            .Select(value => value.ShippingRequestId!.Value)
            .Distinct()
            .ToArray();
        if (candidateShippingRequestIds.Length > 0)
        {
            // The unique database index intentionally prevents two active tracking codes from
            // claiming one shipping request. Deleted, never-sent previews must release that claim.
            await _db.TrackingDispatches
                .Where(value => value.IsDeleted && value.ShippingRequestId.HasValue &&
                    candidateShippingRequestIds.Contains(value.ShippingRequestId.Value))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.ShippingRequestId, (Guid?)null)
                    .SetProperty(value => value.UpdatedAt, DateTime.UtcNow), ct);

            var alreadyClaimedShippingRequestIds = await _db.TrackingDispatches.AsNoTracking()
                .Where(value => !value.IsDeleted && value.ShippingRequestId.HasValue &&
                    candidateShippingRequestIds.Contains(value.ShippingRequestId.Value) &&
                    (value.Status == TrackingDispatchStatus.Ready ||
                     value.Status == TrackingDispatchStatus.Sending ||
                     value.Status == TrackingDispatchStatus.Sent))
                .Select(value => value.ShippingRequestId!.Value)
                .Distinct()
                .ToArrayAsync(ct);

            foreach (var dispatch in batch.Dispatches.Where(value =>
                         value.ShippingRequestId.HasValue &&
                         alreadyClaimedShippingRequestIds.Contains(value.ShippingRequestId.Value)))
            {
                dispatch.Status = TrackingDispatchStatus.NeedsReview;
                dispatch.MatchNotes =
                    "این درخواست ارسال قبلاً به کد رهگیری دیگری متصل شده است؛ برای جلوگیری از ارسال اشتباه نیازمند بررسی است";
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
        try
        {
            await ReportUnmatchedTrackingDispatchesAsync(message.Chat.Id, batch.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Reporting unmatched tracking recipients failed for batch {BatchId}; preview processing will continue.",
                batch.Id);
            try
            {
                await ReplyAsync(message.Chat.Id,
                    "⚠️ ارسال گزارش گیرنده‌های پیدانشده به گروه «خطا فاکتور نرسیده» با خطای فنی روبه‌رو شد؛ پیش‌نمایش و پردازش فایل ادامه پیدا می‌کند.", ct);
            }
            catch (Exception notificationException)
            {
                _logger.LogWarning(notificationException,
                    "Could not notify source chat about unmatched tracking report failure for batch {BatchId}.",
                    batch.Id);
            }
        }
        await SendTrackingBatchPreviewAsync(message.Chat.Id, batch.Id, true, ct, duplicateCount);
        return true;
    }

    private async Task ReportUnmatchedTrackingDispatchesAsync(
        long sourceChatId, Guid batchId, CancellationToken ct)
    {
        var failureChatId = string.IsNullOrWhiteSpace(_options.InvoiceFailureChatId)
            ? _options.AdminChatId.Trim()
            : _options.InvoiceFailureChatId.Trim();
        if (string.IsNullOrWhiteSpace(failureChatId))
        {
            _logger.LogWarning(
                "Unmatched tracking recipients in batch {BatchId} could not be reported because no invoice failure chat is configured.",
                batchId);
            await ReplyAsync(sourceChatId,
                "⚠️ گیرنده یک یا چند کد رهگیری پیدا نشد، اما گروه «خطا فاکتور نرسیده» تنظیم نشده است.", ct);
            return;
        }

        var unmatched = await _db.TrackingDispatches.AsNoTracking()
            .Where(value => value.ImportBatchId == batchId && !value.IsDeleted &&
                value.Status == TrackingDispatchStatus.NeedsReview && !value.CustomerId.HasValue)
            .OrderBy(value => value.RecipientName)
            .ToArrayAsync(ct);
        if (unmatched.Length == 0) return;

        var failedReports = 0;
        foreach (var dispatch in unmatched)
        {
            try
            {
                var carrier = dispatch.Carrier switch
                {
                    TrackingCarrier.IranPost => "پست",
                    TrackingCarrier.IranPostExpress => "پست ویژه",
                    TrackingCarrier.Chapar => "چاپار",
                    _ => "نامشخص"
                };
                var caption =
                    $"⚠️ گیرنده کد رهگیری پیدا نشد\n\n" +
                    $"نام خوانده‌شده: {dispatch.RecipientName}\n" +
                    $"کد رهگیری: {dispatch.TrackingCode}\n" +
                    $"شرکت ارسال: {carrier}" +
                    (string.IsNullOrWhiteSpace(dispatch.Destination)
                        ? ""
                        : $"\nمقصد: {dispatch.Destination}") +
                    $"\nعلت: {dispatch.MatchNotes}\n" +
                    $"شناسه داخلی: {dispatch.Id:N}";
                TelegramSendResult report;
                if (dispatch.CardImage is { Length: > 0 })
                {
                    report = await _sender.SendPhotoBytesWithKeyboardAsync(
                        failureChatId, dispatch.CardImage,
                        $"tracking-unmatched-{dispatch.TrackingCode}.png", caption,
                        BuildTrackingDispatchButtons(dispatch), ct);
                }
                else
                {
                    report = await _sender.SendInlineKeyboardAsync(
                        failureChatId, caption, BuildTrackingDispatchButtons(dispatch), ct);
                }

                if (!report.IsSuccessful)
                {
                    failedReports++;
                    _logger.LogWarning(
                        "Unmatched tracking recipient report failed for dispatch {DispatchId}: {Error}",
                        dispatch.Id, report.Error);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failedReports++;
                _logger.LogError(exception,
                    "Unmatched tracking recipient report failed for dispatch {DispatchId}.", dispatch.Id);
            }
        }

        if (failedReports > 0)
        {
            await ReplyAsync(sourceChatId,
                $"⚠️ گیرنده {unmatched.Length} کد رهگیری پیدا نشد؛ گزارش {failedReports} مورد به گروه «خطا فاکتور نرسیده» هم ناموفق بود. پیش‌نمایش‌ها حفظ شده‌اند.", ct);
        }
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
                    $"کد: {dispatch.TrackingCode}\n{dispatch.MatchNotes}",
                    BuildTrackingDispatchButtons(dispatch), ct);
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

    private async Task ConfirmTrackingBatchAsync(
        long adminChatId, Guid batchId, CancellationToken ct, Guid? onlyDispatchId = null)
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
                (!onlyDispatchId.HasValue || value.Id == onlyDispatchId.Value) &&
                (value.Status == TrackingDispatchStatus.Ready || value.Status == TrackingDispatchStatus.Failed))
            .Select(value => value.Id).ToArrayAsync(ct);
        var sentCount = 0;
        var failedCount = 0;
        var failureDetails = new List<string>();
        foreach (var id in ids)
        {
            try
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

            var chatIds = await ResolveTrackingDestinationChatIdsAsync(dispatch, ct);
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
                var delivery = await _db.TrackingDispatchDeliveries.AsNoTracking()
                    .FirstOrDefaultAsync(value => value.TrackingDispatchId == dispatch.Id &&
                        value.TelegramChatId == chatId, ct);
                if (delivery?.SentAt is not null) continue;
                if (delivery?.AttemptedAt is not null)
                {
                    await _db.TrackingDispatchDeliveries
                        .Where(value => value.Id == delivery.Id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(value => value.LastError,
                                "نتیجه تلاش قبلی نامطمئن است؛ برای جلوگیری از ارسال تکراری خودکار تکرار نشد")
                            .SetProperty(value => value.UpdatedAt, DateTime.UtcNow), ct);
                    continue;
                }

                var now = DateTime.UtcNow;
                if (delivery is null)
                {
                    var deliveryId = Guid.NewGuid();
                    await _db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO TrackingDispatchDeliveries
                            (Id, TrackingDispatchId, TelegramChatId, AttemptedAt, CreatedAt, UpdatedAt, IsDeleted)
                        SELECT {deliveryId}, {dispatch.Id}, {chatId}, {now}, {now}, {now}, CAST(0 AS bit)
                        WHERE NOT EXISTS (
                            SELECT 1 FROM TrackingDispatchDeliveries WITH (UPDLOCK, HOLDLOCK)
                            WHERE TrackingDispatchId = {dispatch.Id} AND TelegramChatId = {chatId});
                        """, ct);
                }
                else
                {
                    var claimedDelivery = await _db.TrackingDispatchDeliveries
                        .Where(value => value.Id == delivery.Id && value.AttemptedAt == null && value.SentAt == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(value => value.AttemptedAt, now)
                            .SetProperty(value => value.UpdatedAt, now), ct);
                    if (claimedDelivery == 0) continue;
                }

                delivery = await _db.TrackingDispatchDeliveries.AsNoTracking()
                    .FirstAsync(value => value.TrackingDispatchId == dispatch.Id &&
                        value.TelegramChatId == chatId, ct);
                if (delivery.AttemptedAt is null || delivery.SentAt is not null) continue;
                var result = await _sender.SendPhotoBytesWithKeyboardAsync(chatId, dispatch.CardImage,
                    $"tracking-{dispatch.TrackingCode}.png",
                    $"📦 کد رهگیری مرسوله شما\n{dispatch.TrackingCode}" +
                    (string.IsNullOrWhiteSpace(dispatch.TrackingUrl) ? "" : $"\n{dispatch.TrackingUrl}"), [], ct);
                var completedAt = DateTime.UtcNow;
                await _db.TrackingDispatchDeliveries.Where(value => value.Id == delivery.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.SentAt,
                            result.IsSuccessful ? completedAt : (DateTime?)null)
                        .SetProperty(value => value.LastError,
                            result.IsSuccessful ? null : result.Error)
                        .SetProperty(value => value.UpdatedAt, completedAt), ct);
            }

            var sentDestinationCount = await _db.TrackingDispatchDeliveries.AsNoTracking()
                .CountAsync(value => value.TrackingDispatchId == dispatch.Id &&
                    value.SentAt.HasValue && chatIds.Contains(value.TelegramChatId), ct);
            if (sentDestinationCount == chatIds.Length)
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
                var deliveryErrors = await _db.TrackingDispatchDeliveries.AsNoTracking()
                    .Where(value => value.TrackingDispatchId == dispatch.Id &&
                        !value.SentAt.HasValue && chatIds.Contains(value.TelegramChatId))
                    .Select(value => value.LastError)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct()
                    .ToArrayAsync(ct);
                failureDetails.Add($"{dispatch.RecipientName}: مرحله ارسال تلگرام — " +
                    (deliveryErrors.Length == 0 ? dispatch.LastError : string.Join("؛ ", deliveryErrors)));
            }
            dispatch.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Tracking dispatch processing failed for {DispatchId} in batch {BatchId}.", id, batchId);
                _db.ChangeTracker.Clear();
                var technicalError = $"خطای فنی در مرحله ارسال؛ شناسه پیگیری داخلی: {id:N}";
                try
                {
                    await _db.TrackingDispatches
                        .Where(value => value.Id == id && value.Status != TrackingDispatchStatus.Sent)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(value => value.Status, TrackingDispatchStatus.Failed)
                            .SetProperty(value => value.LastError, technicalError)
                            .SetProperty(value => value.UpdatedAt, DateTime.UtcNow), ct);
                }
                catch (Exception persistenceException)
                {
                    _logger.LogError(persistenceException,
                        "Could not persist tracking dispatch failure for {DispatchId}.", id);
                }
                failedCount++;
                failureDetails.Add($"شناسه {id:N}: {technicalError}");
            }
        }
        await ReplyAsync(adminChatId,
            $"{(onlyDispatchId.HasValue ? "نتیجه ارسال این کد رهگیری:" : "نتیجه ارسال کد رهگیری:")}\n✅ موفق: {sentCount}\n⚠️ ناموفق: {failedCount}\n" +
            "موارد مبهم و تکراری ارسال نشدند." +
            (failureDetails.Count == 0 ? "" : $"\n\nجزئیات خطا:\n{string.Join("\n", failureDetails.Take(15).Select(value => $"• {value}"))}"), ct);
    }

    private async Task ConfirmTrackingDispatchAsync(
        long adminChatId, long previewMessageId, Guid dispatchId, CancellationToken ct)
    {
        var dispatch = await _db.TrackingDispatches.AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == dispatchId && !value.IsDeleted, ct);
        if (dispatch is null)
        {
            await ReplyAsync(adminChatId, "این کد رهگیری حذف شده یا پیدا نشد.", ct);
            return;
        }
        if (dispatch.Status == TrackingDispatchStatus.NeedsReview)
        {
            await ReplyAsync(adminChatId,
                "⚠️ این مورد هنوز نیازمند بررسی است؛ ابتدا «ساخت مجدد» را بزنید.", ct);
            return;
        }
        if (dispatch.Status == TrackingDispatchStatus.Sent)
        {
            await _sender.EditReplyMarkupAsync(adminChatId.ToString(), previewMessageId,
                BuildTrackingDispatchButtons(dispatch), ct);
            await ReplyAsync(adminChatId, "این کد قبلاً ارسال شده و دوباره ارسال نشد.", ct);
            return;
        }

        await ConfirmTrackingBatchAsync(adminChatId, dispatch.ImportBatchId, ct, dispatch.Id);
        _db.ChangeTracker.Clear();
        dispatch = await _db.TrackingDispatches.AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == dispatchId, ct);
        if (dispatch is not null)
            await _sender.EditReplyMarkupAsync(adminChatId.ToString(), previewMessageId,
                BuildTrackingDispatchButtons(dispatch), ct);
    }

    private async Task RebuildTrackingDispatchAsync(
        long adminChatId, long previewMessageId, Guid dispatchId, CancellationToken ct)
    {
        var dispatch = await _db.TrackingDispatches.Include(value => value.Deliveries)
            .FirstOrDefaultAsync(value => value.Id == dispatchId && !value.IsDeleted, ct);
        if (dispatch is null)
        {
            await ReplyAsync(adminChatId, "این کد رهگیری حذف شده یا پیدا نشد.", ct);
            return;
        }
        if (dispatch.Status == TrackingDispatchStatus.Sent || dispatch.Deliveries.Any(value => value.SentAt.HasValue))
        {
            await ReplyAsync(adminChatId, "این کد قبلاً ارسال شده و قابل ساخت مجدد نیست.", ct);
            return;
        }

        var recheckedName = await _trackingImportService.RecheckRecipientNameAsync(
            dispatch.CardImage ?? [], ct);
        var nameWasCorrected = !string.IsNullOrWhiteSpace(recheckedName) &&
                               !string.Equals(dispatch.RecipientName, recheckedName, StringComparison.Ordinal);
        if (nameWasCorrected)
            dispatch.RecipientName = recheckedName!;
        var match = await _trackingImportService.MatchAsync(
            dispatch.RecipientName, dispatch.Destination, ct);
        var hasUncertainAttempt = dispatch.Deliveries.Any(value => value.AttemptedAt.HasValue && !value.SentAt.HasValue);
        dispatch.CustomerId = match.CustomerId;
        dispatch.ShippingRequestId = match.ShippingRequestId;
        dispatch.Status = hasUncertainAttempt
            ? TrackingDispatchStatus.Failed
            : match.Status;
        dispatch.MatchNotes = hasUncertainAttempt
            ? "تلاش قبلی نتیجه قطعی ندارد؛ برای جلوگیری از ارسال تکراری، ارسال مجدد مسدود است"
            : $"{match.Notes} — بازبینی مجدد" + (nameWasCorrected ? " و اصلاح نام گیرنده" : "");
        dispatch.LastError = hasUncertainAttempt ? dispatch.MatchNotes : null;
        if (dispatch.Carrier != TrackingCarrier.IranPost)
        {
            dispatch.CardImage = _trackingImportService.BuildCard(
                dispatch.Carrier, dispatch.RecipientName, dispatch.TrackingCode, dispatch.TrackingUrl);
        }
        dispatch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var caption = $"{(dispatch.Status == TrackingDispatchStatus.Ready ? "✅" : "⚠️")} {dispatch.RecipientName}\n" +
                      $"کد: {dispatch.TrackingCode}\n{dispatch.MatchNotes}";
        var previewUpdate = await _sender.EditPhotoBytesWithKeyboardAsync(
            adminChatId.ToString(), previewMessageId, dispatch.CardImage!,
            $"tracking-{dispatch.TrackingCode}.png", caption,
            BuildTrackingDispatchButtons(dispatch), ct);
        if (!previewUpdate.IsSuccessful)
            _logger.LogWarning("Rebuilt tracking preview update failed for {DispatchId}: {Error}",
                dispatch.Id, previewUpdate.Error);
        await ReplyAsync(adminChatId,
            dispatch.Status == TrackingDispatchStatus.Ready
                ? "✅ تطبیق دوباره بررسی شد و این مورد آماده ارسال است."
                : $"⚠️ بازبینی انجام شد اما هنوز قابل ارسال امن نیست: {dispatch.MatchNotes}", ct);
    }

    private async Task DeleteTrackingDispatchAsync(
        string callbackId, long adminChatId, long previewMessageId, Guid dispatchId, CancellationToken ct)
    {
        var dispatch = await _db.TrackingDispatches.Include(value => value.Deliveries)
            .FirstOrDefaultAsync(value => value.Id == dispatchId && !value.IsDeleted, ct);
        if (dispatch is null)
        {
            await _sender.AnswerCallbackAsync(callbackId, "این مورد قبلاً حذف شده است.", ct, true);
            return;
        }
        if (dispatch.Status == TrackingDispatchStatus.Sent || dispatch.Deliveries.Any(value => value.SentAt.HasValue))
        {
            await _sender.AnswerCallbackAsync(callbackId,
                "کد ارسال‌شده قابل حذف نیست.", ct, true);
            return;
        }

        dispatch.IsDeleted = true;
        dispatch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _sender.AnswerCallbackAsync(callbackId, "کد رهگیری حذف شد.", ct);
        var deleted = await _sender.DeleteMessageAsync(
            adminChatId.ToString(), previewMessageId, ct);
        if (!deleted.IsSuccessful)
            await _sender.EditReplyMarkupAsync(adminChatId.ToString(), previewMessageId, [], ct);
    }

    private static bool TryParseTrackingDispatchCallback(
        string callbackData, string action, out Guid dispatchId)
    {
        var prefix = $"trackingimport:{action}:";
        dispatchId = Guid.Empty;
        return callbackData.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(callbackData[prefix.Length..], "N", out dispatchId);
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildTrackingDispatchButtons(
        TrackingDispatch dispatch)
    {
        if (dispatch.IsDeleted) return [];
        if (dispatch.Status == TrackingDispatchStatus.Sent)
            return [new[] { new TelegramInlineButton("☑️ ارسال شد", "trackingimport:noop") }];
        return
        [
            new[]
            {
                new TelegramInlineButton("✅ تأیید و ارسال", $"trackingimport:send:{dispatch.Id:N}"),
                new TelegramInlineButton("🔄 ساخت مجدد", $"trackingimport:rebuild:{dispatch.Id:N}"),
                new TelegramInlineButton("🗑 حذف", $"trackingimport:delete:{dispatch.Id:N}")
            }
        ];
    }

    private async Task<string[]> ResolveTrackingDestinationChatIdsAsync(
        TrackingDispatch dispatch, CancellationToken ct)
    {
        if (dispatch.ShippingRequestId.HasValue)
        {
            var registrationChatId = await _db.OrderItems.AsNoTracking()
                .Where(value => !value.IsDeleted &&
                    value.ShippingRequestId == dispatch.ShippingRequestId &&
                    value.Order != null && value.Order.DeliveryAddress != null)
                .OrderByDescending(value => value.ShippingRequestedAt ?? value.CreatedAt)
                .Select(value => value.Order!.DeliveryAddress!.RegistrationTelegramChatId)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(registrationChatId)) return [registrationChatId];
        }

        var addressOwnerCustomerId = dispatch.CustomerId!.Value;
        var addressRegistrationChatId = await _db.Addresses.AsNoTracking()
            .Where(value => !value.IsDeleted && value.CustomerId == addressOwnerCustomerId &&
                value.RegistrationTelegramChatId != null)
            .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => value.RegistrationTelegramChatId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(addressRegistrationChatId))
            return [addressRegistrationChatId];

        // The address owner is authoritative. Prefer its newest linked group and do not
        // broadcast a tracking card to every historical group connection.
        var directChatId = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive &&
                value.CustomerId == addressOwnerCustomerId)
            .OrderByDescending(value => value.LastSeenAt ?? value.LinkedAt)
            .Select(value => value.ChatId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(directChatId)) return [directChatId];

        // Legacy imports can contain duplicate customer rows for the same username.
        // Recover the group through the exact normalized username of the address owner.
        var ownerUsername = await _db.Customers.AsNoTracking()
            .Where(value => !value.IsDeleted && value.Id == addressOwnerCustomerId)
            .Select(value => value.Username)
            .FirstOrDefaultAsync(ct);
        var normalizedUsername = ownerUsername?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUsername)) return [];

        var matchingCustomerIds = await _db.Customers.AsNoTracking()
            .Where(value => !value.IsDeleted && value.Username != null &&
                (value.Username.ToLower() == normalizedUsername ||
                 value.Username.ToLower() == "@" + normalizedUsername))
            .Select(value => value.Id)
            .ToArrayAsync(ct);
        var usernameChatId = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive &&
                matchingCustomerIds.Contains(value.CustomerId))
            .OrderByDescending(value => value.LastSeenAt ?? value.LinkedAt)
            .Select(value => value.ChatId)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(usernameChatId) ? [] : [usernameChatId];
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
