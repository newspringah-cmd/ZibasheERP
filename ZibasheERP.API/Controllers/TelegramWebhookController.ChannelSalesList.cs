using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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
                    await ConfirmBottleOwnerGiftAsync(callback, requestId, cancellationToken);
                else
                    await ShowChannelBottleSelectionAsync(callback, requestId, cancellationToken);
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
                {
                    _temporaryMessageCleaner.Cancel(callback.Message.Chat.Id.ToString(), callback.Message.MessageId);
                    _temporaryMessageCleaner.ReleaseInteraction(
                        callback.Message.Chat.Id.ToString(), callback.Message.MessageId, callback.From.Id);
                }
                if (request is not null)
                    await RefreshChannelSalesListAsync(request.SalesListId, cancellationToken);
                await _sender.AnswerCallbackAsync(callback.Id, "درخواست لغو شد.", cancellationToken);
                return true;
            }
            if (parts[0] == "slr" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out listId))
            {
                var canRefresh = IsPrimaryOwner(callback.From.Id) ||
                    await _sender.IsChatAdministratorAsync(
                        _options.AdminChatId, callback.From.Id.ToString(), cancellationToken);
                if (!canRefresh)
                    throw new InvalidOperationException("رفرش دستی فقط برای مدیران فعال است؛ تغییرات لیست به‌صورت خودکار نمایش داده می‌شود.");
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
                "لیست هم‌زمان تغییر کرد و خودکار به‌روز می‌شود؛ چند لحظه بعد دوباره انتخاب کنید.",
                cancellationToken);
            return true;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه معتبر نیست.", cancellationToken);
        return true;
    }

    private async Task StartChannelReservationAsync(
        TelegramCallbackQuery callback, Guid salesListId, int volume, CancellationToken cancellationToken)
    {
        var initialList = await _salesListRepository.GetByIdAsync(salesListId, cancellationToken)
            ?? throw new InvalidOperationException("لیست فروش پیدا نشد.");
        var membershipChatId = initialList.TelegramChannelId ?? _options.SalesChannelId;
        if (!await _sender.IsChatMemberAsync(membershipChatId, callback.From.Id.ToString(), cancellationToken))
            throw new InvalidOperationException("برای ثبت درخواست باید عضو کانال فروش باشید.");
        var salesList = initialList;
        if (salesList.Status != SalesListStatus.Open || volume < salesList.MinimumRequestVolumeMl || volume > salesList.RemainingVolume)
            throw new InvalidOperationException($"این مقدار قابل ثبت نیست؛ باقی‌مانده {salesList.RemainingVolume} میل است.");
        if (!salesList.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(salesList.TelegramChannelId))
            throw new InvalidOperationException("پست کانال این لیست پیدا نشد.");
        if (!_temporaryMessageCleaner.TryAcquireInteraction(
                salesList.TelegramChannelId, salesList.TelegramMessageId.Value,
                callback.From.Id, TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("کاربر دیگری در حال ثبت می‌باشد؛ لطفاً منتظر بمانید.");
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

        if (salesList.IsInventoryOffer)
        {
            if (!salesList.FixedBottleId.HasValue)
                throw new InvalidOperationException("شیشه ثابت این موجودی مشخص نشده است.");
            var bottle = salesList.FixedBottle
                ?? throw new InvalidOperationException("شیشه این موجودی دیگر فعال نیست.");
            var bottlePrice = salesList.FixedBottlePrice
                ?? throw new InvalidOperationException("قیمت ثابت شیشه این موجودی مشخص نیست.");
            await _salesListRequestRepository.SelectBottleAsync(
                request.Id, request.TelegramUserId, bottle.Id, bottlePrice, cancellationToken);
            request = await _salesListRequestRepository.GetAsync(request.Id, cancellationToken) ?? request;
            await ShowChannelConfirmationAsync(
                callback, request, $"{BottleLabel(bottle.Type.ToString())} — {bottlePrice:N0} تومان", cancellationToken);
            return;
        }

        var warning = sameVolumeCount > 0
            ? $"⚠️ شما قبلاً {sameVolumeCount} بار مقدار {volume} میل را در این لیست ثبت کرده‌اید.\n"
            : totalPrevious > 0
                ? $"ℹ️ شما در حال حاضر مجموعاً {totalPrevious} میل در این لیست دارید.\n"
                : string.Empty;
        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>
        {
            new[] { new TelegramInlineButton("👤 برای خودم", $"slp:{EncodeCompactGuid(request.Id)}:self") }
        };
        if (salesList.HasBottleOwner)
            rows[0] = new[]
            {
                new TelegramInlineButton("👤 برای خودم", $"slp:{EncodeCompactGuid(request.Id)}:self"),
                new TelegramInlineButton("🎁 هدیه برای صاحب باتل", $"slp:{EncodeCompactGuid(request.Id)}:gift")
            };
        rows.Add(new[] { new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}") });
        var prompt = salesList.HasBottleOwner
            ? $"کاربر {DisplayTelegramUser(callback.From)}\n{warning}این {volume} میل را برای خودتان ثبت می‌کنید یا هدیه برای صاحب باتل است؟"
            : $"کاربر {DisplayTelegramUser(callback.From)}\n{warning}این {volume} میل را برای خودتان ثبت می‌کنید؟";
        var originalRequests = await _salesListRequestRepository.GetConfirmedAsync(salesList.Id, cancellationToken);
        var channelResult = await _sender.EditPhotoCaptionAsync(
            salesList.TelegramChannelId, salesList.TelegramMessageId.Value, prompt, rows, cancellationToken);
        if (!channelResult.IsSuccessful)
            throw new InvalidOperationException("نمایش گزینه‌ها روی پست لیست ناموفق بود.");
        _temporaryMessageCleaner.ScheduleRestore(
            salesList.TelegramChannelId, salesList.TelegramMessageId.Value,
            FormatChannelSalesList(salesList, originalRequests), BuildChannelVolumeButtons(salesList),
            TimeSpan.FromSeconds(7));
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
        rows.Add(new[] { new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}") });
        if (rows.Count == 1)
            throw new InvalidOperationException("برای این حجم شیشه فعالی تعریف نشده است.");
        await EditRequestPromptAsync(request,
            $"کاربر {DisplayTelegramUser(callback.From)}\nنوع شیشه را انتخاب کنید:", rows, cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
    }

    private async Task ConfirmBottleOwnerGiftAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (request.TelegramUserId != callback.From.Id.ToString())
            throw new InvalidOperationException("این دکمه متعلق به کاربر دیگری است.");
        var confirmed = await _salesListRequestRepository.GetConfirmedAsync(request.SalesListId, cancellationToken);
        var owner = confirmed.FirstOrDefault(value => value.IsBottleOwner)
            ?? throw new InvalidOperationException("صاحب باتل برای این لیست مشخص نشده است.");
        var identity = !string.IsNullOrWhiteSpace(owner.TelegramUsername)
            ? $"@{owner.TelegramUsername.TrimStart('@')}" : owner.TelegramUserId;
        await _salesListRequestRepository.SetGiftRecipientAsync(
            request.Id, callback.From.Id.ToString(), identity, cancellationToken);
        request = await _salesListRequestRepository.GetAsync(request.Id, cancellationToken) ?? request;
        request.BottleId = null;
        request.BottlePrice = 0;
        await _salesListRequestRepository.SaveChangesAsync(cancellationToken);
        await _salesListRequestRepository.ConfirmCurrentBottleAsync(
            request.Id, callback.From.Id.ToString(), cancellationToken);
        if (request.SalesList.TelegramMessageId.HasValue && !string.IsNullOrWhiteSpace(request.SalesList.TelegramChannelId))
        {
            _temporaryMessageCleaner.Cancel(request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value);
            _temporaryMessageCleaner.ReleaseInteraction(
                request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value, callback.From.Id);
        }
        await RefreshChannelSalesListAsync(request.SalesListId, cancellationToken);
        var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
            ? _options.AdminChatId : _options.SalesAuditChatId;
        var tehranNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran");
        await _sender.SendAsync(auditChatId,
            "🎁 هدیه جدید برای صاحب باتل\n" +
            $"زمان: {tehranNow:yyyy/MM/dd HH:mm:ss}\n" +
            $"هدیه‌دهنده: {DisplayTelegramUser(callback.From)}\n" +
            $"هدیه‌گیرنده: {identity}\n" +
            $"کد لیست: {request.SalesList.PublicCode}\n" +
            $"عطر: {request.SalesList.EnglishName}\n" +
            $"مقدار: {request.VolumeMl} میل\nشیشه: رایگان", cancellationToken);
        await _sender.AnswerCallbackAsync(
            callback.Id, $"هدیه برای {identity} ثبت شد ✅", cancellationToken, showAlert: true);
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
            TimeSpan.FromSeconds(7));
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
            TimeSpan.FromSeconds(7));
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
        {
            _temporaryMessageCleaner.Cancel(request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value);
            _temporaryMessageCleaner.ReleaseInteraction(
                request.SalesList.TelegramChannelId, request.SalesList.TelegramMessageId.Value, callback.From.Id);
        }
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
            var captions = FormatChannelSalesListPages(salesList, requests);
            await SynchronizeContinuationPostAsync(salesList, captions.Continuation, cancellationToken);
            await _sender.EditPhotoCaptionAsync(
                salesList.TelegramChannelId,
                salesList.TelegramMessageId.Value,
                captions.Main,
                BuildChannelVolumeButtons(salesList, salesList.TelegramContinuationMessageId),
                cancellationToken);
            await SendRemainingVolumeAlertsAsync(salesList, cancellationToken);
            if (salesList.Status == SalesListStatus.Full)
                await CompleteAndRollSalesListAsync(salesList, requests, cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task SendRemainingVolumeAlertsAsync(
        SalesList list, CancellationToken cancellationToken)
    {
        if (!list.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(list.TelegramChannelId))
            return;

        var changed = false;
        var postUrl = BuildTelegramDiscussionMessageUrl(
            list.TelegramChannelId, list.TelegramMessageId.Value);

        if (list.RemainingVolume < 25 &&
            list.LowStockAlertSentAt is null &&
            !string.IsNullOrWhiteSpace(_options.LowStockAlertChatId))
        {
            var result = await _sender.SendHtmlAsync(
                _options.LowStockAlertChatId,
                "⚠️ <b>هشدار کاهش موجودی لیست فروش</b>\n\n" +
                $"عطر: {Html(list.EnglishName)}\n" +
                $"باقی‌مانده: <b>{list.RemainingVolume} میل</b>\n\n" +
                $"<a href=\"{Html(postUrl)}\">مشاهده پست در کانال</a>",
                cancellationToken);
            if (result.IsSuccessful)
            {
                list.LowStockAlertSentAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (list.RemainingVolume < 11 &&
            list.PromotionAlertSentAt is null &&
            !string.IsNullOrWhiteSpace(_options.PromotionAlertChatId))
        {
            var result = await _sender.SendHtmlAsync(
                _options.PromotionAlertChatId,
                "📣 <b>این لیست نیاز به تبلیغات دارد</b>\n\n" +
                $"عطر: {Html(list.EnglishName)}\n" +
                $"فقط <b>{list.RemainingVolume} میل</b> باقی مانده است.\n\n" +
                $"<a href=\"{Html(postUrl)}\">مشاهده پست و شروع تبلیغات</a>",
                cancellationToken);
            if (result.IsSuccessful)
            {
                list.PromotionAlertSentAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (!changed) return;
        list.UpdatedAt = DateTime.UtcNow;
        await _salesListRepository.UpdateAsync(list, cancellationToken);
        await _salesListRepository.SaveChangesAsync(cancellationToken);
    }

    private async Task SynchronizeContinuationPostAsync(
        SalesList list, string? continuationCaption, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(list.TelegramChannelId) || !list.TelegramMessageId.HasValue)
            return;
        if (string.IsNullOrWhiteSpace(continuationCaption))
        {
            if (!list.TelegramContinuationMessageId.HasValue) return;
            await _sender.DeleteMessageAsync(
                list.TelegramChannelId, list.TelegramContinuationMessageId.Value, cancellationToken);
            list.TelegramContinuationMessageId = null;
            await _salesListRepository.UpdateAsync(list, cancellationToken);
            await _salesListRepository.SaveChangesAsync(cancellationToken);
            return;
        }
        var mainUrl = BuildTelegramDiscussionMessageUrl(list.TelegramChannelId, list.TelegramMessageId.Value);
        var navigation = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("⬅️ بازگشت به پست اصلی", Url: mainUrl) }
        };
        if (list.TelegramContinuationMessageId.HasValue)
        {
            await _sender.EditPhotoCaptionAsync(
                list.TelegramChannelId, list.TelegramContinuationMessageId.Value,
                continuationCaption, navigation, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)) return;
        var result = await _sender.SendPhotoWithKeyboardAsync(
            list.TelegramChannelId, list.TelegramPhotoFileId,
            continuationCaption, navigation, cancellationToken);
        if (!result.IsSuccessful || !result.MessageId.HasValue)
            throw new InvalidOperationException($"ساخت پست ادامه لیست ناموفق بود: {result.Error}");
        list.TelegramContinuationMessageId = result.MessageId.Value;
        await _salesListRepository.UpdateAsync(list, cancellationToken);
        await _salesListRepository.SaveChangesAsync(cancellationToken);
    }

    private async Task CompleteAndRollSalesListAsync(
        SalesList completed, IReadOnlyCollection<SalesListRequest> requests, CancellationToken ct)
    {
        var finalCaption = "✅ لیست فروش تکمیل شد\n\n" + FormatChannelSalesList(completed, requests);
        var completedListsChatId = string.IsNullOrWhiteSpace(_options.CompletedSalesListsChatId)
            ? _options.AdminChatId : _options.CompletedSalesListsChatId;
        if (!string.IsNullOrWhiteSpace(completed.TelegramPhotoFileId))
            await _sender.SendPhotoHtmlAsync(
                completedListsChatId, completed.TelegramPhotoFileId, finalCaption, ct);
        else
            await _sender.SendHtmlAsync(completedListsChatId, finalCaption, ct);

        completed.Status = SalesListStatus.Closed;
        completed.ClosedDate = DateTime.UtcNow;
        completed.UpdatedAt = DateTime.UtcNow;
        await _salesListRepository.UpdateAsync(completed, ct);
        await _salesListRepository.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(completed.TelegramChannelId))
        {
            if (completed.TelegramDiscussionMessageId.HasValue)
                await _sender.DeleteMessageAsync(completed.TelegramChannelId, completed.TelegramDiscussionMessageId.Value, ct);
            if (completed.TelegramContinuationMessageId.HasValue)
                await _sender.DeleteMessageAsync(completed.TelegramChannelId, completed.TelegramContinuationMessageId.Value, ct);
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
        var discussionText =
            $"💬 هر سؤالی در رابطه با عطر «{nextList.EnglishName}» دارید، اینجا بپرسید.\n" +
            "اگر مقدار موردنظر شما در دکمه‌ها نیست، آن را در کامنت بنویسید تا ادمین ثبت کند.";
        var discussion = await _sender.SendReplyAsync(
            _options.SalesChannelId, discussionText, post.MessageId!.Value, ct);
        if (discussion.IsSuccessful) nextList.TelegramDiscussionMessageId = discussion.MessageId;
        await _salesListRepository.UpdateAsync(nextList, ct);
        await _salesListRepository.SaveChangesAsync(ct);
        await ReplyAsync(long.Parse(_options.AdminChatId),
            $"لیست بعدی به‌صورت خودکار منتشر شد ✅\nکد جدید: {nextList.PublicCode}\nصاحب باتل: {DisplayUser(queue[0])} — {queue[0].VolumeMl} میل", ct);
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildChannelVolumeButtons(
        SalesList list, long? continuationMessageId = null)
    {
        continuationMessageId ??= list.TelegramContinuationMessageId;
        var values = ChannelVolumes
            .Where(value => value >= list.MinimumRequestVolumeMl && value <= list.RemainingVolume)
            .ToArray();
        var rows = values.Chunk(3)
            .Select(valuesRow => (IReadOnlyCollection<TelegramInlineButton>)valuesRow.Select(value =>
                new TelegramInlineButton($"{value} ml", $"slv:{EncodeCompactGuid(list.Id)}:{value}")).ToArray())
            .ToList();
        rows.Add(new[] { new TelegramInlineButton("🔄 رفرش (ادمین)", $"slr:{EncodeCompactGuid(list.Id)}") });
        if (continuationMessageId.HasValue && !string.IsNullOrWhiteSpace(list.TelegramChannelId))
            rows.Add(new[]
            {
                new TelegramInlineButton("ادامه فهرست سفارش‌ها ➡️",
                    Url: BuildTelegramDiscussionMessageUrl(list.TelegramChannelId, continuationMessageId.Value))
            });
        return rows;
    }

    private static string FormatChannelSalesList(
        SalesList list, IReadOnlyCollection<SalesListRequest> requests)
        => FormatChannelSalesListPages(list, requests).Main;

    private static (string Main, string? Continuation) FormatChannelSalesListPages(
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
        var header = $"{linkedName}\n{brandTag}\n{gender}\nL.{list.ReleaseYear}\n\n" +
            $"{HtmlClipped(list.PersianName, 55)}\n\n" +
            $"🍊 نت‌های ابتدایی: {HtmlClipped(list.TopNotes, 40)}\n\n" +
            $"🌸 نت‌های میانی: {HtmlClipped(list.MiddleNotes, 40)}\n\n" +
            $"🌳 نت‌های پایانی: {HtmlClipped(list.BaseNotes, 40)}\n\n" +
            $"🎼 آکوردها: {HtmlClipped(list.Accords, 40)}\n\n" +
            $"حجم کل: {list.TotalVolume}ml\n\nقیمت هر میل: {list.PricePerMl:N0} تومان\n\n" +
            $"حداقل درخواست: {list.MinimumRequestVolumeMl} میل\n\n" +
            $"باقی‌مانده: {list.RemainingVolume} میل";
        var nextUsers = next.Select(item => Html(DisplayUser(item))).ToArray();
        var nextSection = nextUsers.Length == 0
            ? "Next Bottle:\n\nاولین نفر صف باتل باشید 😘😘"
            : BuildCompactUserLine("Next Bottle", nextUsers, 220);
        if (header.Length > 760)
            header = $"{linkedName}\n\n{brandTag} | {gender} | L.{list.ReleaseYear}\n\n" +
                $"{HtmlClipped(list.PersianName, 35)}\n\n" +
                $"🍊 {HtmlClipped(list.TopNotes, 22)} | 🌸 {HtmlClipped(list.MiddleNotes, 22)}\n\n" +
                $"🌳 {HtmlClipped(list.BaseNotes, 22)} | 🎼 {HtmlClipped(list.Accords, 22)}\n\n" +
                $"حجم: {list.TotalVolume}ml | هر میل: {list.PricePerMl:N0} تومان\n\n" +
                $"حداقل: {list.MinimumRequestVolumeMl} میل\n\n" +
                $"باقی‌مانده: {list.RemainingVolume} میل";
        var availableForRoster = Math.Max(0, safeCaptionLength - header.Length - nextSection.Length - 4);
        var rosterLines = rosterGroups.SelectMany(group =>
            new[] { $"{group.Volume} ml:" }.Concat(group.Users)).ToArray();
        var mainLines = new List<string>();
        var continuationLines = new List<string>();
        string? currentVolumeHeading = null;
        var headingAddedToContinuation = false;
        foreach (var line in rosterLines)
        {
            if (line.EndsWith(" ml:", StringComparison.Ordinal))
            {
                currentVolumeHeading = line;
                headingAddedToContinuation = false;
            }
            var candidate = string.Join("\n", mainLines.Append(line));
            if (candidate.Length <= availableForRoster)
                mainLines.Add(line);
            else
            {
                if (!line.EndsWith(" ml:", StringComparison.Ordinal) &&
                    currentVolumeHeading is not null &&
                    !headingAddedToContinuation)
                {
                    continuationLines.Add(currentVolumeHeading);
                    headingAddedToContinuation = true;
                }
                continuationLines.Add(line);
                if (line.EndsWith(" ml:", StringComparison.Ordinal))
                    headingAddedToContinuation = true;
            }
        }
        var main = string.Join("\n\n", new[]
        {
            header, string.Join("\n", mainLines), nextSection
        }.Where(value => value.Length > 0));
        if (continuationLines.Count == 0) return (main, null);
        var continuationHeader = $"ادامه فهرست سفارش‌ها\n{HtmlClipped(list.EnglishName, 60)}\n\n";
        var shown = new List<string>();
        foreach (var line in continuationLines)
        {
            if ((continuationHeader + string.Join("\n", shown.Append(line))).Length > safeCaptionLength - 16)
                break;
            shown.Add(line);
        }
        var omitted = continuationLines.Count - shown.Count;
        var continuation = continuationHeader + string.Join("\n", shown) +
            (omitted > 0 ? $"\n… +{omitted} مورد" : string.Empty);
        return (main, continuation);
    }

    private static string BuildCompactUserLine(string label, string[] users, int maximumLength)
    {
        var shown = new List<string>();
        foreach (var user in users)
        {
            var candidate = $"{label}:\n\n{string.Join("، ", shown.Append(user))}";
            if (candidate.Length > maximumLength - 14) break;
            shown.Add(user);
        }
        var omitted = users.Length - shown.Count;
        return $"{label}:\n\n{string.Join("، ", shown)}" +
            (omitted > 0 ? $" … +{omitted} نفر" : string.Empty);
    }

    private static string DisplayUser(SalesListRequest request) =>
        (string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}") +
        (request.IsGift ? $" for {GiftRecipientLabel(request)}" : string.Empty) +
        (request.IsBottleOwner ? " 👑" : string.Empty) +
        (request.Bottle?.Type == BottleType.Fancy ? " F" : string.Empty);
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
        var elementStarts = StringInfo.ParseCombiningCharacters(text);
        if (elementStarts.Length > maximumCharacters)
        {
            var retainedElements = Math.Max(0, maximumCharacters - 1);
            var endIndex = retainedElements == 0 ? 0 : elementStarts[retainedElements];
            text = text[..endIndex] + "…";
        }
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
