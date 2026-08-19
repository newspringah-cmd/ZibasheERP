using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.RegularExpressions;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static readonly int[] ChannelVolumes = [1, 2, 3, 4, 5, 7, 10, 15, 20, 30, 50];

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
            if (parts[0] == "sly" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out requestId))
            {
                await ConfirmChannelReservationAsync(callback, requestId, cancellationToken);
                return true;
            }
            if (parts[0] == "sln" && parts.Length == 2 && TryDecodeCompactGuid(parts[1], out requestId))
            {
                await _salesListRequestRepository.CancelAsync(requestId, callback.From.Id.ToString(), cancellationToken);
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
        if (string.IsNullOrWhiteSpace(_options.SalesDiscussionChatId))
            throw new InvalidOperationException("گروه گفت‌وگوی فروش هنوز تنظیم نشده است.");

        var salesList = await _salesListRepository.GetByIdAsync(salesListId, cancellationToken)
            ?? throw new InvalidOperationException("لیست فروش پیدا نشد.");
        if (salesList.Status != SalesListStatus.Open || volume < salesList.MinimumRequestVolumeMl || volume > salesList.RemainingVolume)
            throw new InvalidOperationException($"این مقدار قابل ثبت نیست؛ باقی‌مانده {salesList.RemainingVolume} میل است.");

        var bottles = await _mediator.Send(new GetAvailableBottlesQuery(volume), cancellationToken);
        if (bottles.Count == 0)
            throw new InvalidOperationException("برای این حجم شیشه فعالی تعریف نشده است.");

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
        var rows = bottles.Select(bottle =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{volume} میل {BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان",
                    $"slb:{EncodeCompactGuid(request.Id)}:{EncodeCompactGuid(bottle.Id)}")
            }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}")
            }).ToArray();
        var prompt = $"کاربر {DisplayTelegramUser(callback.From)}\n{warning}برای درخواست {volume} میل، نوع شیشه را انتخاب کنید:";
        var privateResult = await _sender.SendInlineKeyboardAsync(
            callback.From.Id.ToString(), prompt, rows, cancellationToken);
        if (privateResult.IsSuccessful)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "گزینه‌های شیشه در گفت‌وگوی خصوصی ربات ارسال شد.", cancellationToken);
            return;
        }
        await _sender.SendInlineKeyboardAsync(_options.SalesDiscussionChatId, prompt, rows, cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id,
            "ابتدا ربات را Start کنید؛ گزینه‌ها فعلاً در بخش گفت‌وگو ارسال شد.", cancellationToken);
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

        var previous = await _salesListRequestRepository.GetConfirmedForUserAsync(
            request.SalesListId, request.TelegramUserId, cancellationToken);
        var duplicate = previous.Any(value => value.VolumeMl == request.VolumeMl)
            ? $"⚠️ قبلاً همین مقدار را ثبت کرده‌اید. با تأیید، {request.VolumeMl} میل دیگر اضافه می‌شود.\n"
            : string.Empty;
        var total = request.PerfumePricePerMl * request.VolumeMl + bottle.Price;
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
            $"عطر: {request.VolumeMl} میل × {request.PerfumePricePerMl:N0} = {request.VolumeMl * request.PerfumePricePerMl:N0} تومان\n" +
            $"شیشه: {BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان\n" +
            $"مبلغ کل: {total:N0} تومان";
        var privateResult = await _sender.SendInlineKeyboardAsync(
            callback.From.Id.ToString(), confirmation, rows, cancellationToken);
        if (!privateResult.IsSuccessful)
            await _sender.SendInlineKeyboardAsync(_options.SalesDiscussionChatId, confirmation, rows, cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, "نوع شیشه انتخاب شد.", cancellationToken);
    }

    private async Task ConfirmChannelReservationAsync(
        TelegramCallbackQuery callback, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await _salesListRequestRepository.GetAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("درخواست پیدا نشد.");
        if (!request.BottleId.HasValue)
            throw new InvalidOperationException("ابتدا نوع شیشه را انتخاب کنید.");
        await _salesListRequestRepository.ConfirmCurrentBottleAsync(
            request.Id, callback.From.Id.ToString(), cancellationToken);
        await RefreshChannelSalesListAsync(request.SalesListId, cancellationToken);
        var confirmed = await _salesListRequestRepository.GetAsync(request.Id, cancellationToken)
            ?? request;
        var bottleText = confirmed.Bottle is null
            ? "نامشخص"
            : $"{BottleLabel(confirmed.Bottle.Type.ToString())} — {confirmed.BottlePrice:N0} تومان";
        var total = confirmed.VolumeMl * confirmed.PerfumePricePerMl + confirmed.BottlePrice;
        var tehranNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran");
        await _sender.SendAsync(_options.AdminChatId,
            "✅ ثبت جدید در لیست فروش\n" +
            $"زمان: {tehranNow:yyyy/MM/dd HH:mm:ss}\n" +
            $"کاربر: {DisplayTelegramUser(callback.From)}\n" +
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
            BaseNotes = completed.BaseNotes, Accords = completed.Accords, BatchId = completed.BatchId,
            PricePerMl = completed.PricePerMl, TotalVolume = completed.TotalVolume,
            MinimumRequestVolumeMl = completed.MinimumRequestVolumeMl,
            ReservedVolume = Math.Min(queue[0].VolumeMl, completed.TotalVolume),
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
        var roster = requests
            .Where(value => value.Kind == SalesListRequestKind.CurrentBottle)
            .GroupBy(value => value.VolumeMl)
            .OrderByDescending(value => value.Key)
            .Select(value => $"{value.Key} ml:\n" + string.Join("\n", value.Select(item => Html(DisplayUser(item)))));
        var next = requests.Where(value => value.Kind == SalesListRequestKind.NextBottle).ToArray();
        var gender = list.Gender switch
        {
            PerfumeGender.Women => "#women 👩",
            PerfumeGender.Men => "#men 👨",
            _ => "#unisex 👩‍🦰👨"
        };
        var englishName = Html(list.EnglishName);
        var linkedName = string.IsNullOrWhiteSpace(list.ProductPageUrl)
            ? englishName
            : $"<a href=\"{Html(list.ProductPageUrl)}\">{englishName}</a>";
        var brandTag = "#" + Html(ToHashtag(list.DisplayBrand));
        return $"کد: <b>{list.PublicCode}</b>\n" +
            $"{linkedName}\n{brandTag}\n{gender}\nL.{list.ReleaseYear}\n\n" +
            $"{Html(list.PersianName)}\n\n" +
            $"🍊 نت‌های ابتدایی: {Html(list.TopNotes)}\n" +
            $"🌸 نت‌های میانی: {Html(list.MiddleNotes)}\n" +
            $"🌳 نت‌های پایانی: {Html(list.BaseNotes)}\n" +
            $"🎼 آکوردها: {Html(list.Accords)}\n\n" +
            $"حجم کل: {list.TotalVolume}ml\nقیمت هر میل: {list.PricePerMl:N0} تومان\n" +
            $"حداقل میل درخواستی: {list.MinimumRequestVolumeMl} میل\nباقی‌مانده: {list.RemainingVolume} میل\n\n" +
            string.Join("\n\n", roster) +
            "\n\nNext Bottle:\n" +
            (next.Length == 0
                ? "اولین نفر صف باتل باشید 😘😘"
                : string.Join("\n", next.Select(item => Html(DisplayUser(item)))));
    }

    private static string DisplayUser(SalesListRequest request) =>
        string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}";
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
    private static string ToHashtag(string value) =>
        Regex.Replace(value.Trim(), @"[^\p{L}\p{N}]+", "_").Trim('_');
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
