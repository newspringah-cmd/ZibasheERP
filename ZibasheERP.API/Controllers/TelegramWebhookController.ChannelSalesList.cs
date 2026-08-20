using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static readonly int[] ChannelVolumes = [1, 2, 3, 4, 5, 7, 10, 15, 20, 30, 50];
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SalesListRefreshLocks = new();

    private async Task<bool> TryHandleChannelSalesListCallbackAsync(
        TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        var data = callback.Data ?? string.Empty;
        if (!data.StartsWith("sl", StringComparison.Ordinal))
            return false;

        try
        {
            var parts = data.Split(':');
            if (parts[0] == "slv" && parts.Length == 3 &&
                TryDecodeCompactGuid(parts[1], out var listId) && int.TryParse(parts[2], out var volume))
            {
                await StartChannelReservationAsync(callback, listId, volume, cancellationToken);
                return true;
            }
            if (parts[0] == "slb" && parts.Length == 3 &&
                TryDecodeCompactGuid(parts[1], out var requestId) && TryDecodeCompactGuid(parts[2], out var bottleId))
            {
                await SelectChannelBottleAsync(callback, requestId, bottleId, cancellationToken);
                return true;
            }
            if (parts[0] == "slp" && parts.Length == 3 &&
                TryDecodeCompactGuid(parts[1], out requestId))
            {
                if (parts[2] == "gift")
                    await StartGiftRecipientInputAsync(callback, requestId, cancellationToken);
                else
                    await ShowChannelBottleSelectionAsync(callback, requestId, cancellationToken);
                return true;
            }
            if (parts[0] == "slf" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out requestId))
            {
                await SelectFreeBottleForOwnerGiftAsync(callback, requestId, cancellationToken);
                return true;
            }
            if (parts[0] == "sly" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out requestId))
            {
                await ConfirmChannelReservationAsync(callback, requestId, cancellationToken);
                return true;
            }
            if (parts[0] == "sln" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out requestId))
            {
                var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken);
                await _salesListRequestRepository.CancelAsync(requestId, callback.From.Id.ToString(), cancellationToken);
                if (callback.Message is { MessageId: > 0 })
                    _temporaryMessageCleaner.Cancel(callback.Message.Chat.Id.ToString(), callback.Message.MessageId);
                if (request is not null)
                    await RefreshChannelSalesListAsync(request.SalesListId, cancellationToken);
                await _sender.AnswerCallbackAsync(callback.Id, "درخواست لغو شد.", cancellationToken);
                return true;
            }
            if (parts[0] == "slr" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out listId))
            {
                await RefreshChannelSalesListAsync(listId, cancellationToken);
                await _sender.AnswerCallbackAsync(callback.Id, "لیست به‌روز شد.", cancellationToken);
                return true;
            }
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "لیست هم‌زمان تغییر کرد؛ دکمه Refresh را بزنید و دوباره انتخاب کنید.",
                cancellationToken);
            return true;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه معتبر نیست.", cancellationToken);
        return true;
    }

    private async Task StartChannelReservationAsync(
        TelegramCallbackQuery callback, Guid salesListId, int volume, CancellationToken cancellationToken)
    {
        if (!await _sender.IsChatMemberAsync(_options.SalesChannelId, callback.From.Id.ToString(), cancellationToken))
            throw new InvalidOperationException("برای ثبت درخواست باید عضو کانال فروش باشید.");
        var salesList = await _salesListRepository.GetByIdAsync(salesListId, cancellationToken)
            ?? throw new InvalidOperationException("لیست فروش پیدا نشد.");
        if (salesList.Status != SalesListStatus.Open || volume < salesList.MinimumRequestVolumeMl || volume > salesList.RemainingVolume)
            throw new InvalidOperationException($"این مقدار قابل ثبت نیست؛ باقی‌مانده {salesList.RemainingVolume} میل است.");
        if (!salesList.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(salesList.TelegramChannelId))
            throw new InvalidOperationException("پست کانال این لیست پیدا نشد.");
        if (_temporaryMessageCleaner.IsScheduled(salesList.TelegramChannelId, salesList.TelegramMessageId.Value))
            throw new InvalidOperationException("کاربر دیگری در حال ثبت این لیست است؛ حداکثر ۱۵ ثانیه بعد دوباره تلاش کنید.");
        if (!string.IsNullOrWhiteSpace(callback.Id))
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);

        var previous = await _salesListRequestRepository.GetConfirmedForUserAsync(
            salesListId, callback.From.Id.ToString(), cancellationToken);
        var sameVolumeCount = previous.Count(value => value.VolumeMl == volume);
        var totalPrevious = previous.Sum(value => value.VolumeMl);
        var request = new SalesListRequest
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            SalesListId = salesListId,
            TelegramUserId = callback.From.Id.ToString(),
            TelegramUsername = NormalizeUsername(callback.From.Username),
            VolumeMl = volume,
            PerfumePricePerMl = salesList.PricePerMl,
            Kind = SalesListRequestKind.CurrentBottle,
            Status = SalesListRequestStatus.PendingConfirmation,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            ExternalReference = $"telegram-channel:{Guid.NewGuid():N}"
        };
        await _salesListRequestRepository.AddAsync(request, cancellationToken);
        await _salesListRequestRepository.SaveChangesAsync(cancellationToken);

        var warning = sameVolumeCount > 0
            ? $"⚠️ شما قبلاً {sameVolumeCount} بار مقدار {volume} میل را در این لیست ثبت کرده‌اید.\n"
            : totalPrevious > 0
                ? $"ℹ️ شما در حال حاضر مجموعاً {totalPrevious} میل در این لیست دارید.\n"
                : string.Empty;
        var rows = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[]
            {
                new TelegramInlineButton("👤 برای خودم", $"slp:{EncodeCompactGuid(request.Id)}:self"),
                new TelegramInlineButton("🎁 هدیه", $"slp:{EncodeCompactGuid(request.Id)}:gift")
            },
            new[] { new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}") }
        };
        var prompt = $"کاربر {DisplayTelegramUser(callback.From)}\n{warning}این {volume} میل را برای خودتان ثبت می‌کنید یا هدیه است؟";
        var originalRequests = await _salesListRequestRepository.GetConfirmedAsync(salesList.Id, cancellationToken);
        var channelResult = await _sender.EditPhotoCaptionAsync(
            salesList.TelegramChannelId, salesList.TelegramMessageId.Value, prompt, rows, cancellationToken);
        if (!channelResult.IsSuccessful)
            throw new InvalidOperationException("نمایش گزینه‌ها روی پست لیست ناموفق بود.");
        _temporaryMessageCleaner.ScheduleRestore(
            salesList.TelegramChannelId, salesList.TelegramMessageId.Value,
            FormatChannelSalesList(salesList, originalRequests), BuildChannelVolumeButtons(salesList),
            TimeSpan.FromSeconds(5));
    }

    private async Task ShowChannelBottleSelectionAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != callback.From.Id.ToString())
            throw new InvalidOperationException("این دکمه متعلق به کاربر دیگری است.");
        var bottles = await _mediator.Send(new GetAvailableBottlesQuery(request.VolumeMl), cancellationToken);
        var rows = bottles.Select(bottle =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{request.VolumeMl} میل {BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان",
                    $"slb:{EncodeCompactGuid(request.Id)}:{EncodeCompactGuid(bottle.Id)}")
            }).ToList();
        if (await _salesListRequestRepository.IsGiftRecipientBottleOwnerAsync(request.Id, cancellationToken))
            rows.Add(new[] { new TelegramInlineButton("🎁 هدیه برای صاحب باتل — شیشه رایگان", $"slf:{EncodeCompactGuid(request.Id)}") });
        rows.Add(new[] { new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}") });
        if (rows.Count == 1)
            throw new InvalidOperationException("برای این حجم شیشه فعالی تعریف نشده است.");
        var gift = request.IsGift
            ? $"\nهدیه برای: {GiftRecipientLabel(request)}"
            : string.Empty;
        await EditRequestPromptAsync(request,
            $"کاربر {DisplayTelegramUser(callback.From)}{gift}\nنوع شیشه را انتخاب کنید:", rows, cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
    }

    private async Task StartGiftRecipientInputAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != callback.From.Id.ToString())
            throw new InvalidOperationException("این دکمه متعلق به کاربر دیگری است.");
        var prompt = await _sender.SendForceReplyAsync(_options.SalesDiscussionChatId,
            $"🎁 ثبت هدیه — درخواست {request.Id.ToString("N")[..8]}\n" +
            $"عطر: {request.SalesList.EnglishName}\nکد لیست: {request.SalesList.PublicCode}\nمقدار: {request.VolumeMl} میل\n" +
            $"هدیه‌دهنده: {DisplayTelegramUser(callback.From)}\n\n" +
            "شناسه هدیه‌گیرنده را به‌صورت @username یا Telegram ID ارسال کنید؛ نیازی به Reply نیست.\n" +
            "مهلت ثبت: ۲ دقیقه", cancellationToken);
        if (!prompt.IsSuccessful || !prompt.MessageId.HasValue)
            throw new InvalidOperationException("ارسال فرم هدیه در گروه گفت‌وگو ناموفق بود.");
        _giftRecipientDrafts.Set(new TelegramGiftRecipientDraft(
            request.Id, request.SalesListId, callback.From.Id, prompt.MessageId.Value, DateTime.UtcNow.AddMinutes(2)));
        var discussionUrl = BuildTelegramDiscussionMessageUrl(_options.SalesDiscussionChatId, prompt.MessageId.Value);
        await _sender.AnswerCallbackAsync(
            callback.Id,
            $"هدیه {request.VolumeMl} میل از «{request.SalesList.EnglishName}» — کد لیست {request.SalesList.PublicCode}\n" +
            "دکمه روی پست را بزنید و شناسه هدیه‌گیرنده را در گروه گفتگو ارسال کنید.",
            cancellationToken: cancellationToken,
            showAlert: true);
        await EditRequestPromptAsync(request,
            $"🎁 ثبت هدیه {request.VolumeMl} میل\nعطر: {request.SalesList.EnglishName}\nکد لیست: {request.SalesList.PublicCode}\n" +
            "دکمه زیر را بزنید و شناسه هدیه‌گیرنده را در گروه گفتگو ارسال کنید:",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✍️ واردکردن شناسه هدیه‌گیرنده", Url: discussionUrl) },
                new[] { new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}") }
            }, cancellationToken);
    }

    private async Task<bool> TryHandleGiftRecipientMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (message.From is null || !_giftRecipientDrafts.TryGet(message.From.Id, out var draft))
            return false;
        if (message.Chat.Id.ToString() != _options.SalesDiscussionChatId)
            return false;
        var identity = message.Text?.Trim() ?? string.Empty;
        var valid = identity.StartsWith('@') && identity.Length > 1 ||
                    !identity.StartsWith('@') && new string(identity.Where(char.IsDigit).ToArray()).Length >= 5;
        if (!valid)
        {
            await ReplyAsync(message.Chat.Id, "شناسه معتبر نیست؛ @username یا Telegram ID را وارد کنید.", cancellationToken);
            return true;
        }
        await _salesListRequestRepository.SetGiftRecipientAsync(
            draft.RequestId, message.From.Id.ToString(), identity, cancellationToken);
        _giftRecipientDrafts.Remove(message.From.Id);
        await _sender.DeleteMessageAsync(
            _options.SalesDiscussionChatId, draft.PromptMessageId, cancellationToken);
        if (message.MessageId > 0)
            await _sender.DeleteMessageAsync(
                _options.SalesDiscussionChatId, message.MessageId, cancellationToken);
        var request = await _salesListRequestRepository.GetAsync(draft.RequestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        var callback = new TelegramCallbackQuery(string.Empty, message.From, message, $"slp:{EncodeCompactGuid(request.Id)}:self");
        await ShowChannelBottleSelectionAsync(callback, request.Id, cancellationToken);
        return true;
    }

    private async Task SelectFreeBottleForOwnerGiftAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != callback.From.Id.ToString() ||
            !await _salesListRequestRepository.IsGiftRecipientBottleOwnerAsync(request.Id, cancellationToken))
            throw new InvalidOperationException("هدیه‌گیرنده صاحب باتل این لیست نیست.");
        request.BottleId = null;
        request.BottlePrice = 0;
        await _salesListRequestRepository.SaveChangesAsync(cancellationToken);
        await ShowChannelConfirmationAsync(callback, request, "هدیه برای صاحب باتل — شیشه رایگان", cancellationToken);
    }

    private async Task SelectChannelBottleAsync(
        TelegramCallbackQuery callback, Guid requestId, Guid bottleId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != callback.From.Id.ToString())
            throw new InvalidOperationException("این دکمه متعلق به کاربر دیگری است.");
        var bottles = await _mediator.Send(new GetAvailableBottlesQuery(request.VolumeMl), cancellationToken);
        var bottle = bottles.FirstOrDefault(value => value.Id == bottleId)
            ?? throw new InvalidOperationException("این شیشه دیگر قابل انتخاب نیست.");
        await _salesListRequestRepository.SelectBottleAsync(
            request.Id, request.TelegramUserId, bottle.Id, bottle.Price, cancellationToken);
        request = await _salesListRequestRepository.GetAsync(request.Id, cancellationToken) ?? request;
        await ShowChannelConfirmationAsync(
            callback, request, $"{BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان", cancellationToken);
    }

    private async Task ShowChannelConfirmationAsync(
        TelegramCallbackQuery callback, SalesListRequest request, string bottleDescription,
        CancellationToken cancellationToken)
    {
        var previous = await _salesListRequestRepository.GetConfirmedForUserAsync(
            request.SalesListId, request.TelegramUserId, cancellationToken);
        var duplicate = previous.Any(value => value.VolumeMl == request.VolumeMl)
            ? $"⚠️ قبلاً همین مقدار را ثبت کرده‌اید. با تأیید، {request.VolumeMl} میل دیگر اضافه می‌شود.\n"
            : string.Empty;
        var total = request.PerfumePricePerMl * request.VolumeMl + request.BottlePrice;
        var rows = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[]
            {
                new TelegramInlineButton("✅ بله، ثبت شود", $"sly:{EncodeCompactGuid(request.Id)}"),
                new TelegramInlineButton("❌ خیر", $"sln:{EncodeCompactGuid(request.Id)}")
            }
        };
        var confirmation =
            $"کاربر {DisplayTelegramUser(callback.From)}، آیا از ثبت این درخواست مطمئن هستید؟\n\n" +
            duplicate +
            (request.IsGift ? $"🎁 هدیه برای: {GiftRecipientLabel(request)}\n" : string.Empty) +
            $"عطر: {request.VolumeMl} میل × {request.PerfumePricePerMl:N0} = {request.VolumeMl * request.PerfumePricePerMl:N0} تومان\n" +
            $"شیشه: {bottleDescription}\n" +
            $"مبلغ کل: {total:N0} تومان";
        if (!request.SalesList.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(request.SalesList.TelegramChannelId))
            throw new InvalidOperationException("پست کانال این لیست پیدا نشد.");
        _temporaryMessageCleaner.Cancel(request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value);
        var originalRequests = await _salesListRequestRepository.GetConfirmedAsync(request.SalesListId, cancellationToken);
        var channelResult = await _sender.EditPhotoCaptionAsync(
            request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value,
            confirmation, rows, cancellationToken);
        if (!channelResult.IsSuccessful)
            throw new InvalidOperationException("نمایش تأیید روی پست لیست ناموفق بود.");
        _temporaryMessageCleaner.ScheduleRestore(
            request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value,
            FormatChannelSalesList(request.SalesList, originalRequests), BuildChannelVolumeButtons(request.SalesList),
            TimeSpan.FromSeconds(5));
        if (!string.IsNullOrWhiteSpace(callback.Id))
            await _sender.AnswerCallbackAsync(callback.Id, "نوع شیشه انتخاب شد.", cancellationToken);
    }

    private async Task EditRequestPromptAsync(
        SalesListRequest request, string prompt,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken)
    {
        if (!request.SalesList.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(request.SalesList.TelegramChannelId))
            throw new InvalidOperationException("پست کانال این لیست پیدا نشد.");
        _temporaryMessageCleaner.Cancel(request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value);
        var original = await _salesListRequestRepository.GetConfirmedAsync(request.SalesListId, cancellationToken);
        var result = await _sender.EditPhotoCaptionAsync(
            request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value,
            prompt, rows, cancellationToken);
        if (!result.IsSuccessful)
            throw new InvalidOperationException("نمایش گزینه‌ها روی پست لیست ناموفق بود.");
        _temporaryMessageCleaner.ScheduleRestore(
            request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value,
            FormatChannelSalesList(request.SalesList, original), BuildChannelVolumeButtons(request.SalesList),
            TimeSpan.FromSeconds(5));
    }

    private static string GiftRecipientLabel(SalesListRequest request) =>
        !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
            ? "@" + request.GiftRecipientTelegramUsername
            : request.GiftRecipientTelegramUserId ?? "نامشخص";

    private async Task ConfirmChannelReservationAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (!request.BottleId.HasValue && !request.IsBottleOwner &&
            !await _salesListRequestRepository.IsGiftRecipientBottleOwnerAsync(request.Id, cancellationToken))
            throw new InvalidOperationException("ابتدا نوع شیشه را انتخاب کنید.");
        await _salesListRequestRepository.ConfirmCurrentBottleAsync(
            request.Id, callback.From.Id.ToString(), cancellationToken);
        if (request.SalesList.TelegramMessageId.HasValue && !string.IsNullOrWhiteSpace(request.SalesList.TelegramChannelId))
            _temporaryMessageCleaner.Cancel(request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value);
        await RefreshChannelSalesListAsync(request.SalesListId, cancellationToken);
        var confirmed = await _salesListRequestRepository.GetAsync(request.Id, cancellationToken)
            ?? request;
        var bottleText = confirmed.IsBottleOwner
            ? "صاحب باتل — رایگان"
            : confirmed.Bottle is null
            ? "نامشخص"
            : $"{BottleLabel(confirmed.Bottle.Type.ToString())} — {confirmed.BottlePrice:N0} تومان";
        var total = confirmed.VolumeMl * confirmed.PerfumePricePerMl + confirmed.BottlePrice;
        var tehranNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran");
        var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
            ? _options.AdminChatId
            : _options.SalesAuditChatId;
        await _sender.SendAsync(auditChatId,
            "✅ ثبت جدید در لیست فروش\n" +
            $"زمان: {tehranNow:yyyy/MM/dd HH:mm:ss}\n" +
            $"کاربر: {DisplayTelegramUser(callback.From)}\n" +
            (confirmed.IsGift ? $"هدیه‌گیرنده: {GiftRecipientLabel(confirmed)}\n" : string.Empty) +
            $"Telegram ID: {callback.From.Id}\n" +
            $"کد لیست: {confirmed.SalesList.PublicCode}\n" +
            $"عطر: {confirmed.SalesList.EnglishName}\n" +
            $"مقدار: {confirmed.VolumeMl} میل\n" +
            $"شیشه: {bottleText}\n" +
            $"مبلغ کل: {total:N0} تومان", cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست با موفقیت ثبت شد ✅", cancellationToken);
    }

    private async Task RefreshChannelSalesListAsync(Guid salesListId, CancellationToken cancellationToken)
    {
        var refreshLock = SalesListRefreshLocks.GetOrAdd(salesListId, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var salesList = await _salesListRepository.GetByIdAsync(salesListId, cancellationToken)
                ?? throw new InvalidOperationException("لیست فروش پیدا نشد.");
            if (!salesList.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(salesList.TelegramChannelId))
                return;
            var requests = await _salesListRequestRepository.GetConfirmedAsync(salesListId, cancellationToken);
            await _sender.EditPhotoCaptionAsync(
                salesList.TelegramChannelId,
                salesList.TelegramMessageId.Value,
                FormatChannelSalesList(salesList, requests),
                BuildChannelVolumeButtons(salesList),
                cancellationToken);
            if (salesList.Status == SalesListStatus.Full)
                await CompleteAndRollSalesListAsync(salesList, requests, cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task CompleteAndRollSalesListAsync(
        SalesList completed, IReadOnlyCollection<SalesListRequest> requests, CancellationToken ct)
    {
        var finalCaption = "✅ لیست فروش تکمیل شد\n\n" + FormatChannelSalesList(completed, requests);
        if (!string.IsNullOrWhiteSpace(completed.TelegramPhotoFileId))
            await _sender.SendPhotoAsync(_options.AdminChatId, completed.TelegramPhotoFileId, finalCaption, ct);
        else
            await ReplyAsync(long.Parse(_options.AdminChatId), finalCaption, ct);

        completed.Status = SalesListStatus.Closed;
        completed.ClosedDate = DateTime.UtcNow;
        completed.UpdatedAt = DateTime.UtcNow;
        await _salesListRepository.UpdateAsync(completed, ct);
        await _salesListRepository.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(completed.TelegramChannelId))
        {
            if (completed.TelegramDiscussionMessageId.HasValue)
                await _sender.DeleteMessageAsync(completed.TelegramChannelId, completed.TelegramDiscussionMessageId.Value, ct);
            if (completed.TelegramMessageId.HasValue)
                await _sender.DeleteMessageAsync(completed.TelegramChannelId, completed.TelegramMessageId.Value, ct);
        }

        var queue = requests.Where(x => x.Kind == SalesListRequestKind.NextBottle)
            .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.CreatedAt).ToArray();
        if (queue.Length == 0 || string.IsNullOrWhiteSpace(completed.TelegramPhotoFileId))
            return;

        int publicCode;
        do publicCode = Random.Shared.Next(10000, 100000);
        while (await _salesListRepository.PublicCodeExistsAsync(publicCode, ct));
        var now = DateTime.UtcNow;
        var nextList = new SalesList
        {
            Id = Guid.NewGuid(), CreatedAt = now, PublicCode = publicCode,
            EnglishName = completed.EnglishName, ProductPageUrl = completed.ProductPageUrl,
            DisplayBrand = completed.DisplayBrand, Gender = completed.Gender, ReleaseYear = completed.ReleaseYear,
            PersianName = completed.PersianName, TopNotes = completed.TopNotes, MiddleNotes = completed.MiddleNotes,
            BaseNotes = completed.BaseNotes, Accords = completed.Accords,
            PerfumeId = completed.PerfumeId, BatchId = null,
            PricePerMl = completed.PricePerMl, TotalVolume = completed.TotalVolume,
            MinimumRequestVolumeMl = completed.MinimumRequestVolumeMl,
            ReservedVolume = Math.Min(queue[0].VolumeMl, completed.TotalVolume),
            HasBottleOwner = true,
            Status = queue[0].VolumeMl >= completed.TotalVolume ? SalesListStatus.Full : SalesListStatus.Open,
            OpenDate = now, TelegramChannelId = _options.SalesChannelId,
            TelegramPhotoFileId = completed.TelegramPhotoFileId, Notes = completed.Notes
        };
        await _salesListRepository.AddAsync(nextList, ct);
        foreach (var (old, index) in queue.Select((value, index) => (value, index)))
        {
            await _salesListRequestRepository.AddAsync(new SalesListRequest
            {
                Id = Guid.NewGuid(), CreatedAt = now, SalesListId = nextList.Id,
                TelegramUserId = old.TelegramUserId, TelegramUsername = old.TelegramUsername,
                IsGift = old.IsGift,
                GiftRecipientTelegramUserId = old.GiftRecipientTelegramUserId,
                GiftRecipientTelegramUsername = old.GiftRecipientTelegramUsername,
                IsBottleOwner = index == 0,
                VolumeMl = old.VolumeMl, PerfumePricePerMl = nextList.PricePerMl,
                Kind = index == 0 ? SalesListRequestKind.CurrentBottle : SalesListRequestKind.NextBottle,
                Status = SalesListRequestStatus.Confirmed, CreatedByAdmin = old.CreatedByAdmin,
                ExpiresAt = DateTime.MaxValue, ConfirmedAt = now.AddTicks(index),
                ExternalReference = $"auto-next:{completed.Id:N}:{old.Id:N}"
            }, ct);
        }
        await _salesListRequestRepository.SaveChangesAsync(ct);
        var nextRequests = await _salesListRequestRepository.GetConfirmedAsync(nextList.Id, ct);
        var post = await _sender.SendPhotoWithKeyboardAsync(_options.SalesChannelId,
            nextList.TelegramPhotoFileId, FormatChannelSalesList(nextList, nextRequests),
            BuildChannelVolumeButtons(nextList), ct);
        if (!post.IsSuccessful)
        {
            await ReplyAsync(long.Parse(_options.AdminChatId), $"ساخت لیست بعدی انجام شد اما انتشار ناموفق بود: {post.Error}", ct);
            return;
        }
        nextList.TelegramMessageId = post.MessageId;
        var discussion = await _sender.SendAsync(_options.SalesChannelId,
            $"💬 هر سؤالی در رابطه با عطر «{nextList.EnglishName}» دارید، اینجا بپرسید.\nکد لیست: {nextList.PublicCode}\n" +
            "اگر مقدار موردنظر شما در دکمه‌ها نیست، آن را در کامنت بنویسید تا ادمین ثبت کند.", ct);
        if (discussion.IsSuccessful) nextList.TelegramDiscussionMessageId = discussion.MessageId;
        await _salesListRepository.UpdateAsync(nextList, ct);
        await _salesListRepository.SaveChangesAsync(ct);
        await ReplyAsync(long.Parse(_options.AdminChatId),
            $"لیست بعدی به‌صورت خودکار منتشر شد ✅\nکد جدید: {nextList.PublicCode}\nصاحب باتل: {DisplayUser(queue[0])} — {queue[0].VolumeMl} میل", ct);
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildChannelVolumeButtons(SalesList list)
    {
        var values = ChannelVolumes
            .Where(value => value >= list.MinimumRequestVolumeMl && value <= list.RemainingVolume)
            .ToArray();
        var rows = values.Chunk(3)
            .Select(valuesRow => (IReadOnlyCollection<TelegramInlineButton>)valuesRow.Select(value =>
                new TelegramInlineButton($"{value} ml", $"slv:{EncodeCompactGuid(list.Id)}:{value}")).ToArray())
            .ToList();
        rows.Add(new[] { new TelegramInlineButton("🔄 Refresh", $"slr:{EncodeCompactGuid(list.Id)}") });
        return rows;
    }

    private static string FormatChannelSalesList(
        SalesList list, IReadOnlyCollection<SalesListRequest> requests)
    {
        const int safeCaptionLength = 1000;
        var rosterGroups = requests
            .Where(value => value.Kind == SalesListRequestKind.CurrentBottle)
            .GroupBy(value => value.VolumeMl)
            .OrderByDescending(value => value.Key)
            .Select(value => (Volume: value.Key, Users: value.Select(item => Html(DisplayUser(item))).ToArray()))
            .ToArray();
        var next = requests.Where(value => value.Kind == SalesListRequestKind.NextBottle).ToArray();
        var gender = list.Gender switch
        {
            PerfumeGender.Women => "#women 👩",
            PerfumeGender.Men => "#men 👨",
            _ => "#unisex 👩‍🦰👨"
        };
        var englishName = HtmlClipped(list.EnglishName, 60);
        var linkedName = string.IsNullOrWhiteSpace(list.ProductPageUrl)
            ? englishName
            : $"<a href=\"{HtmlClipped(list.ProductPageUrl, 180)}\">{englishName}</a>";
        var brandTag = "#" + HtmlClipped(ToHashtag(list.DisplayBrand), 45);
        var header = $"کد: <b>{list.PublicCode}</b>\n" +
            $"{linkedName}\n{brandTag}\n{gender}\nL.{list.ReleaseYear}\n\n" +
            $"{HtmlClipped(list.PersianName, 55)}\n\n" +
            $"🍊 نت‌های ابتدایی: {HtmlClipped(list.TopNotes, 40)}\n" +
            $"🌸 نت‌های میانی: {HtmlClipped(list.MiddleNotes, 40)}\n" +
            $"🌳 نت‌های پایانی: {HtmlClipped(list.BaseNotes, 40)}\n" +
            $"🎼 آکوردها: {HtmlClipped(list.Accords, 40)}\n\n" +
            $"حجم کل: {list.TotalVolume}ml\nقیمت هر میل: {list.PricePerMl:N0} تومان\n" +
            $"حداقل درخواست: {list.MinimumRequestVolumeMl} میل | باقی‌مانده: {list.RemainingVolume} میل";
        var nextUsers = next.Select(item => Html(DisplayUser(item))).ToArray();
        var nextSection = nextUsers.Length == 0
            ? "Next Bottle: اولین نفر صف باتل باشید 😘😘"
            : BuildCompactUserLine("Next Bottle", nextUsers, 220);
        if (header.Length > 760)
            header = $"کد: <b>{list.PublicCode}</b> | {linkedName}\n{brandTag} | {gender} | L.{list.ReleaseYear}\n" +
                $"{HtmlClipped(list.PersianName, 35)}\n" +
                $"🍊 {HtmlClipped(list.TopNotes, 22)} | 🌸 {HtmlClipped(list.MiddleNotes, 22)}\n" +
                $"🌳 {HtmlClipped(list.BaseNotes, 22)} | 🎼 {HtmlClipped(list.Accords, 22)}\n" +
                $"حجم: {list.TotalVolume}ml | هر میل: {list.PricePerMl:N0} تومان\n" +
                $"حداقل: {list.MinimumRequestVolumeMl} | باقی‌مانده: {list.RemainingVolume} میل";
        var availableForRoster = Math.Max(0, safeCaptionLength - header.Length - nextSection.Length - 4);
        var roster = BuildCompactRoster(rosterGroups, availableForRoster);
        return string.Join("\n\n", new[] { header, roster, nextSection }.Where(value => value.Length > 0));
    }

    private static string BuildCompactRoster(
        IReadOnlyCollection<(int Volume, string[] Users)> groups, int maximumLength)
    {
        if (maximumLength <= 0 || groups.Count == 0) return string.Empty;
        var lines = new List<string>();
        var omitted = 0;
        foreach (var group in groups)
        {
            var prefix = $"{group.Volume} ml: ";
            var users = new List<string>();
            foreach (var user in group.Users)
            {
                var candidate = prefix + string.Join("، ", users.Append(user));
                var total = string.Join("\n", lines.Append(candidate)).Length;
                if (total > maximumLength - 14)
                {
                    omitted++;
                    continue;
                }
                users.Add(user);
            }
            if (users.Count > 0) lines.Add(prefix + string.Join("، ", users));
        }
        if (omitted > 0)
        {
            var suffix = $"… +{omitted} نفر";
            if (string.Join("\n", lines.Append(suffix)).Length <= maximumLength)
                lines.Add(suffix);
        }
        return string.Join("\n", lines);
    }

    private static string BuildCompactUserLine(string label, string[] users, int maximumLength)
    {
        var shown = new List<string>();
        foreach (var user in users)
        {
            var candidate = $"{label}: {string.Join("، ", shown.Append(user))}";
            if (candidate.Length > maximumLength - 14) break;
            shown.Add(user);
        }
        var omitted = users.Length - shown.Count;
        return $"{label}: {string.Join("، ", shown)}" + (omitted > 0 ? $" … +{omitted} نفر" : string.Empty);
    }

    private static string DisplayUser(SalesListRequest request) =>
        (string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}") +
        (request.IsGift ? $" for {GiftRecipientLabel(request)}" : string.Empty) +
        (request.IsBottleOwner ? " 👑" : string.Empty);
    private static string DisplayTelegramUser(TelegramUser user)
    {
        var fullName = string.Join(' ', new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var identity = string.IsNullOrWhiteSpace(user.Username) ? user.Id.ToString() : $"@{user.Username}";
        return string.IsNullOrWhiteSpace(fullName) ? identity : $"{fullName} ({identity})";
    }
    private static string? NormalizeUsername(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@');
    private static string BottleLabel(string type) =>
        string.Equals(type, nameof(BottleType.Fancy), StringComparison.OrdinalIgnoreCase) ? "شیشه فانتزی" : "شیشه نرمال";
    private static string EncodeCompactGuid(Guid value) => TelegramCallbackParser.EncodeGuid(value);
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string HtmlClipped(string? value, int maximumCharacters)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length > maximumCharacters)
            text = text[..Math.Max(0, maximumCharacters - 1)] + "…";
        return Html(text);
    }
    private static string ToHashtag(string value) =>
        Regex.Replace(value.Trim(), @"[^\p{L}\p{N}]+", "_").Trim('_');
    private static string BuildTelegramDiscussionMessageUrl(string chatId, long messageId)
    {
        var value = chatId.Trim();
        if (!value.StartsWith("-100", StringComparison.Ordinal) || value.Length <= 4)
            throw new InvalidOperationException("شناسه گروه گفت‌وگو برای ساخت لینک مستقیم معتبر نیست.");
        return $"https://t.me/c/{value[4..]}/{messageId}";
    }
    private static bool TryDecodeCompactGuid(string value, out Guid result)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            result = bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
            return result != Guid.Empty;
        }
        catch (FormatException)
        {
            result = Guid.Empty;
            return false;
        }
    }
}
