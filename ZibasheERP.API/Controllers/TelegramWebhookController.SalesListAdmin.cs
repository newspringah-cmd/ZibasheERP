using System.Globalization;
using MediatR;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Batches.GetBatches;
using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleAdminSalesListCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (callback.Message is null || string.IsNullOrWhiteSpace(callback.Data) ||
            !callback.Data.StartsWith("adminlist:", StringComparison.Ordinal))
            return false;

        var chatId = callback.Message.Chat.Id;
        var userId = callback.From.Id;
        if (!await IsAuthorizedInvoiceAdminAsync(chatId, userId, cancellationToken))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", cancellationToken);
            return true;
        }

        var parts = callback.Data.Split(':');
        switch (parts.ElementAtOrDefault(1))
        {
            case "new":
            case "restart":
                _adminSalesListDrafts.Remove(chatId, userId);
                await SendBatchSelectionAsync(chatId, cancellationToken);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
                return true;

            case "batch" when parts.Length == 3 && Guid.TryParseExact(parts[2], "N", out var batchId):
                await StartSalesListDraftAsync(callback, batchId, cancellationToken);
                return true;

            case "cancel":
                _adminSalesListDrafts.Remove(chatId, userId);
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نویس لغو شد.", cancellationToken);
                await ReplyAsync(chatId, "ساخت لیست فروش لغو شد.", cancellationToken);
                return true;

            case "publish":
                await PublishSalesListDraftAsync(callback, cancellationToken);
                return true;

            default:
                await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", cancellationToken);
                return true;
        }
    }

    private async Task<bool> TryHandleAdminSalesListMessageAsync(
        TelegramMessage message,
        CancellationToken cancellationToken)
    {
        if (message.From is null ||
            !_adminSalesListDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, cancellationToken))
        {
            _adminSalesListDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }

        if (draft.Stage == TelegramAdminSalesListStage.AwaitingPhoto)
        {
            var photo = message.Photo?
                .OrderByDescending(value => (long)value.Width * value.Height)
                .FirstOrDefault();
            if (photo is null)
            {
                await ForceReplyAsync(message.Chat.Id, "لطفاً یک عکس از عطر ارسال کنید؛ فایل یا متن قابل قبول نیست.", cancellationToken);
                return true;
            }

            draft.PhotoFileId = photo.FileId;
            draft.Stage = TelegramAdminSalesListStage.Preview;
            _adminSalesListDrafts.Set(draft);
            await SendSalesListPreviewAsync(draft, cancellationToken);
            return true;
        }

        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return true;
        if (string.Equals(input, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _adminSalesListDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ساخت لیست فروش لغو شد.", cancellationToken);
            return true;
        }

        switch (draft.Stage)
        {
            case TelegramAdminSalesListStage.AwaitingPrice:
                if (!TryParsePositiveDecimal(input, out var price))
                {
                    await ForceReplyAsync(message.Chat.Id, "قیمت معتبر نیست. قیمت هر میل را فقط به تومان وارد کنید؛ مثال: 150000", cancellationToken);
                    return true;
                }
                draft.PricePerMl = price;
                draft.Stage = TelegramAdminSalesListStage.AwaitingVolume;
                _adminSalesListDrafts.Set(draft);
                await ForceReplyAsync(message.Chat.Id, $"حجم قابل فروش را به میل وارد کنید. حداکثر موجودی این بچ: {draft.BatchRemainingVolumeMl:N0} میل", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingVolume:
                if (!TryParsePositiveInt(input, out var volume) || volume > draft.BatchRemainingVolumeMl)
                {
                    await ForceReplyAsync(message.Chat.Id, $"حجم باید عددی مثبت و حداکثر {draft.BatchRemainingVolumeMl:N0} میل باشد.", cancellationToken);
                    return true;
                }
                draft.TotalVolume = volume;
                draft.Stage = TelegramAdminSalesListStage.AwaitingMinimumVolume;
                _adminSalesListDrafts.Set(draft);
                await ForceReplyAsync(message.Chat.Id, "حداقل حجم قابل درخواست مشتری را وارد کنید؛ مثال: 1", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingMinimumVolume:
                if (!TryParsePositiveInt(input, out var minimumVolume) || minimumVolume > draft.TotalVolume)
                {
                    await ForceReplyAsync(message.Chat.Id, $"حداقل حجم باید عددی مثبت و حداکثر {draft.TotalVolume:N0} میل باشد.", cancellationToken);
                    return true;
                }
                draft.MinimumRequestVolumeMl = minimumVolume;
                draft.Stage = TelegramAdminSalesListStage.AwaitingNotes;
                _adminSalesListDrafts.Set(draft);
                await ForceReplyAsync(message.Chat.Id, "توضیحات لیست را وارد کنید. اگر توضیحی ندارید فقط خط تیره (-) بفرستید.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingNotes:
                draft.Notes = input == "-" ? null : input.Length <= 500 ? input : input[..500];
                draft.Stage = TelegramAdminSalesListStage.AwaitingPhoto;
                _adminSalesListDrafts.Set(draft);
                await ForceReplyAsync(message.Chat.Id, "حالا یک عکس واضح از عطر ارسال کنید. همین عکس همراه لیست در کانال اصلی منتشر می‌شود.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.Preview:
                await ReplyAsync(message.Chat.Id, "پیش‌نمایش آماده است؛ یکی از دکمه‌های انتشار، شروع دوباره یا لغو را بزنید.", cancellationToken);
                return true;

            default:
                return true;
        }
    }

    private async Task SendBatchSelectionAsync(long chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SalesChannelId))
        {
            await ReplyAsync(chatId, "شناسه کانال اصلی هنوز در تنظیمات سرور ثبت نشده است.", cancellationToken);
            return;
        }

        var batches = await _mediator.Send(new GetBatchesQuery(50), cancellationToken);
        var candidates = batches
            .Where(batch => string.Equals(batch.Status, "Open", StringComparison.OrdinalIgnoreCase) && batch.RemainingVolumeMl > 0)
            .Take(20)
            .ToArray();
        var available = new List<ZibasheERP.Application.Features.Batches.CreateBatch.BatchResponse>();
        foreach (var batch in candidates)
        {
            if (!await _salesListRepository.HasActiveForBatchAsync(batch.Id, cancellationToken))
                available.Add(batch);
        }
        if (available.Count == 0)
        {
            await ReplyAsync(chatId, "بچ بازی برای ساخت لیست فروش وجود ندارد. ابتدا بچ جدید ثبت کنید.", cancellationToken);
            return;
        }

        var buttons = available.Select(batch =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{batch.PerfumeName} — {batch.BatchNumber} ({batch.RemainingVolumeMl:N0} میل)",
                    $"adminlist:batch:{batch.Id:N}")
            }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton("لغو", "adminlist:cancel")
            }).ToArray();
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), "برای لیست فروش جدید، بچ موردنظر را انتخاب کنید:", buttons, cancellationToken);
    }

    private async Task StartSalesListDraftAsync(
        TelegramCallbackQuery callback,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var batch = (await _mediator.Send(new GetBatchesQuery(100), cancellationToken))
            .FirstOrDefault(value => value.Id == batchId &&
                string.Equals(value.Status, "Open", StringComparison.OrdinalIgnoreCase) &&
                value.RemainingVolumeMl > 0);
        if (batch is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این بچ دیگر قابل انتخاب نیست.", cancellationToken);
            return;
        }

        var draft = new TelegramAdminSalesListDraft
        {
            ChatId = callback.Message!.Chat.Id,
            UserId = callback.From.Id,
            BatchId = batch.Id,
            BatchNumber = batch.BatchNumber,
            PerfumeName = batch.PerfumeName,
            Brand = batch.Brand,
            BatchRemainingVolumeMl = batch.RemainingVolumeMl
        };
        _adminSalesListDrafts.Set(draft);
        await _sender.AnswerCallbackAsync(callback.Id, "بچ انتخاب شد.", cancellationToken);
        await ForceReplyAsync(draft.ChatId, $"بچ «{batch.PerfumeName} — {batch.BatchNumber}» انتخاب شد.\nقیمت فروش هر میل را به تومان وارد کنید؛ مثال: 150000", cancellationToken);
    }

    private async Task SendSalesListPreviewAsync(
        TelegramAdminSalesListDraft draft,
        CancellationToken cancellationToken)
    {
        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("✅ انتشار در کانال اصلی", "adminlist:publish") },
            new[]
            {
                new TelegramInlineButton("🔄 شروع دوباره", "adminlist:restart"),
                new TelegramInlineButton("❌ لغو", "adminlist:cancel")
            }
        };
        var caption = "پیش‌نمایش لیست فروش:\n\n" + FormatSalesListAnnouncement(draft);
        if (!string.IsNullOrWhiteSpace(draft.PhotoFileId))
        {
            var preview = await _sender.SendPhotoAsync(
                draft.ChatId.ToString(), draft.PhotoFileId, caption, cancellationToken);
            if (!preview.IsSuccessful)
            {
                await ReplyAsync(draft.ChatId, $"نمایش عکس ناموفق بود: {preview.Error}", cancellationToken);
                return;
            }
        }
        await _sender.SendInlineKeyboardAsync(
            draft.ChatId.ToString(),
            $"کانال مقصد: {_options.SalesChannelId}\nپس از بررسی عکس و متن، انتشار را تأیید کنید.",
            buttons,
            cancellationToken);
    }

    private async Task PublishSalesListDraftAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!_adminSalesListDrafts.TryGet(chatId, callback.From.Id, out var draft) ||
            draft.Stage != TelegramAdminSalesListStage.Preview)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نویس معتبر پیدا نشد.", cancellationToken);
            return;
        }

        try
        {
            if (!draft.SalesListId.HasValue)
            {
                var created = await _mediator.Send(
                    new CreateSalesListCommand(
                        draft.BatchId,
                        draft.PricePerMl,
                        draft.TotalVolume,
                        _options.SalesChannelId,
                        draft.Notes,
                        draft.MinimumRequestVolumeMl),
                    cancellationToken);
                draft.SalesListId = created.Id;
                _adminSalesListDrafts.Set(draft);
            }

            if (string.IsNullOrWhiteSpace(draft.PhotoFileId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "عکس پیش‌نویس پیدا نشد.", cancellationToken);
                return;
            }
            var salesList = await _salesListRepository.GetByIdAsync(draft.SalesListId.Value, cancellationToken);
            if (salesList is null)
                throw new InvalidOperationException("لیست فروش ساخته شد اما قابل بازیابی نیست.");
            var result = await _sender.SendPhotoWithKeyboardAsync(
                _options.SalesChannelId,
                draft.PhotoFileId,
                FormatChannelSalesList(salesList, Array.Empty<ZibasheERP.Domain.Entities.SalesListRequest>()),
                BuildChannelVolumeButtons(salesList),
                cancellationToken);
            if (!result.IsSuccessful)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "انتشار ناموفق بود؛ دوباره تلاش کنید.", cancellationToken);
                await ReplyAsync(chatId, $"ارسال به کانال اصلی ناموفق بود: {result.Error}", cancellationToken);
                return;
            }

            salesList.TelegramChannelId = _options.SalesChannelId;
            salesList.TelegramMessageId = result.MessageId;
            salesList.UpdatedAt = DateTime.UtcNow;
            await _salesListRepository.UpdateAsync(salesList, cancellationToken);
            await _salesListRepository.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.SalesDiscussionChatId))
            {
                var discussion = await _sender.SendAsync(
                    _options.SalesChannelId,
                    $"💬 پرسش‌ها و درخواست مقدار سفارشی برای {draft.PerfumeName}\n" +
                    $"کد لیست: {salesList.Id.ToString("N")[..8]}\n" +
                    "اگر مقدار موردنظر در دکمه‌ها نیست، آن را در پاسخ به این پیام بنویسید تا ادمین ثبت کند.",
                    cancellationToken);
                if (discussion.IsSuccessful)
                    salesList.TelegramDiscussionMessageId = discussion.MessageId;
                await _salesListRepository.SaveChangesAsync(cancellationToken);
            }

            _adminSalesListDrafts.Remove(chatId, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "منتشر شد ✅", cancellationToken);
            await ReplyAsync(chatId, "لیست فروش با موفقیت در کانال اصلی منتشر شد ✅", cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FluentValidation.ValidationException)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ساخت لیست ناموفق بود.", cancellationToken);
            await ReplyAsync(chatId, exception.Message, cancellationToken);
        }
    }

    private async Task ForceReplyAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        var result = await _sender.SendForceReplyAsync(chatId.ToString(), message, cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram admin wizard prompt failed: {Error}", result.Error);
    }

    private static string FormatSalesListAnnouncement(TelegramAdminSalesListDraft draft) =>
        $"🌿 لیست فروش جدید زیباشی\n" +
        $"🧴 {draft.PerfumeName} — {draft.Brand}\n" +
        $"🏷 بچ: {draft.BatchNumber}\n" +
        $"💧 حجم قابل فروش: {draft.TotalVolume:N0} میل\n" +
        $"📏 حداقل درخواست: {draft.MinimumRequestVolumeMl:N0} میل\n" +
        $"💰 قیمت هر میل: {draft.PricePerMl:N0} تومان" +
        (string.IsNullOrWhiteSpace(draft.Notes) ? string.Empty : $"\n📝 {draft.Notes}");

    private static bool TryParsePositiveDecimal(string value, out decimal result) =>
        decimal.TryParse(NormalizeNumber(value), NumberStyles.Number, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryParsePositiveInt(string value, out int result) =>
        int.TryParse(NormalizeNumber(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;

    private static string NormalizeNumber(string value)
    {
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        const string arabic = "٠١٢٣٤٥٦٧٨٩";
        var chars = value.Trim().Where(character => character is not ',' and not '٬' and not ' ').Select(character =>
        {
            var index = persian.IndexOf(character);
            if (index >= 0) return (char)('0' + index);
            index = arabic.IndexOf(character);
            return index >= 0 ? (char)('0' + index) : character;
        });
        return new string(chars.ToArray());
    }
}
