using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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
                    $"{BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان",
                    $"slb:{EncodeCompactGuid(request.Id)}:{EncodeCompactGuid(bottle.Id)}")
            }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton("❌ انصراف", $"sln:{EncodeCompactGuid(request.Id)}")
            }).ToArray();
        await _sender.SendInlineKeyboardAsync(
            _options.SalesDiscussionChatId,
            $"کاربر {DisplayUser(request)}\n{warning}برای درخواست {volume} میل، نوع شیشه را انتخاب کنید:",
            rows,
            cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, "انتخاب شیشه در بخش گفت‌وگو ارسال شد.", cancellationToken);
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
        await _sender.SendInlineKeyboardAsync(
            _options.SalesDiscussionChatId,
            $"کاربر {DisplayUser(request)}، آیا از ثبت این درخواست مطمئن هستید؟\n\n" +
            duplicate +
            $"عطر: {request.VolumeMl} میل × {request.PerfumePricePerMl:N0} = {request.VolumeMl * request.PerfumePricePerMl:N0} تومان\n" +
            $"شیشه: {BottleLabel(bottle.Type)} — {bottle.Price:N0} تومان\n" +
            $"مبلغ کل: {total:N0} تومان",
            rows,
            cancellationToken);
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
        var perfume = list.Batch.Perfume;
        var roster = requests
            .Where(value => value.Kind == SalesListRequestKind.CurrentBottle)
            .GroupBy(value => value.VolumeMl)
            .OrderByDescending(value => value.Key)
            .Select(value => $"{value.Key} ml:\n" + string.Join("\n", value.Select(DisplayUser)));
        var next = requests.Where(value => value.Kind == SalesListRequestKind.NextBottle).ToArray();
        return $"کد: {list.Id.ToString("N")[..8]}\n" +
            $"{perfume.EnglishName}\n#{perfume.Brand.Replace(' ', '_')}\n\n" +
            $"{perfume.Name}\n{perfume.Notes}\n\n" +
            $"حجم کل: {list.TotalVolume}ml\nقیمت هر میل: {list.PricePerMl:N0} تومان\n" +
            $"حداقل میل درخواستی: {list.MinimumRequestVolumeMl} میل\nباقی‌مانده: {list.RemainingVolume} میل\n\n" +
            string.Join("\n\n", roster) +
            (next.Length == 0 ? string.Empty : "\n\nNext Bottle:\n" + string.Join("\n", next.Select(DisplayUser)));
    }

    private static string DisplayUser(SalesListRequest request) =>
        string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}";
    private static string? NormalizeUsername(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@');
    private static string BottleLabel(string type) =>
        string.Equals(type, nameof(BottleType.Fancy), StringComparison.OrdinalIgnoreCase) ? "شیشه فانتزی" : "شیشه نرمال";
    private static string EncodeCompactGuid(Guid value) => TelegramCallbackParser.EncodeGuid(value);
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
