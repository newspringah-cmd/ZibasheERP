using System.Reflection;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

public static class SmallRemainderReservationTests
{
    public static void Run(Action<bool, string> check)
    {
        var list = new SalesList { Id = Guid.NewGuid(), TotalVolume = 100, ReservedVolume = 98,
            MinimumRequestVolumeMl = 5, Status = SalesListStatus.Open };
        TelegramInlineButton[] VolumeButtons() => ((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)
            typeof(TelegramWebhookController).GetMethod("BuildChannelVolumeButtons", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [list, null])!).SelectMany(row => row)
            .Where(button => button.CallbackData?.StartsWith("slv:") == true).ToArray();

        check(list.EffectiveMinimumRequestVolumeMl == 1 && list.CanReserveVolume(1) && list.CanReserveVolume(2),
            "customers can reserve below the configured minimum when only a small remainder is left");
        var buttons = VolumeButtons();
        check(buttons.Select(button => button.Text).SequenceEqual(["1 ml", "2 ml"]) &&
                buttons.All(button => button.CallbackData!.Contains(TelegramCallbackParser.EncodeGuid(list.Id))),
            "small remainder buttons offer one and two ml on the correct list cycle");
        check(!list.CanReserveVolume(0) && !list.CanReserveVolume(-1) && !list.CanReserveVolume(3),
            "small-remainder exception never permits zero, negative volume or overbooking");

        list.ReservedVolume = 99;
        check(VolumeButtons().Single().Text == "1 ml" && list.CanReserveVolume(1),
            "partial reservation leaves a final one-ml button that can complete the list");
        list.ReservedVolume = 95;
        check(list.EffectiveMinimumRequestVolumeMl == 5 && !list.CanReserveVolume(1) &&
                VolumeButtons().Select(button => button.Text).SequenceEqual(["5 ml"]),
            "remaining volume equal to the minimum keeps the normal minimum");
        list.ReservedVolume = 94;
        check(!list.CanReserveVolume(2) && list.CanReserveVolume(5) &&
                VolumeButtons().Select(button => button.Text).SequenceEqual(["5 ml"]),
            "ordinary reservations cannot use the small-remainder exception too early");
        list.ReservedVolume = 50;
        check(!list.CanReserveVolume(2) && list.MinimumRequestVolumeMl == 5,
            "restored capacity disables stale small-volume requests without changing the configured minimum");
        foreach (var isGift in new[] { false, true })
        {
            var request = new SalesListRequest { VolumeMl = 1, CreatedByAdmin = true, IsGift = isGift };
            check(list.CanReserveVolume(request.VolumeMl, request.CreatedByAdmin),
                $"admin can register a below-minimum {(isGift ? "gift" : "ordinary item")}");
            request.CreatedByAdmin = false;
            check(!list.CanReserveVolume(request.VolumeMl, request.CreatedByAdmin),
                $"customer cannot bypass minimum for a {(isGift ? "gift" : "ordinary item")}");
        }
        check(!list.CanReserveVolume(0, true) && !list.CanReserveVolume(-1, true) &&
                !list.CanReserveVolume(51, true) && list.CanReserveVolume(50, true),
            "admin exemption preserves positive-volume and remaining-capacity limits");
        check(VolumeButtons().All(button => !button.Text.StartsWith("1 ml")) && list.MinimumRequestVolumeMl == 5,
            "admin exemption does not lower public channel buttons or configured minimum");
        list.ReservedVolume = 100;
        check(!list.CanReserveVolume(1) && VolumeButtons().Length == 0,
            "completed list has no reservable volume");
        check(!list.CanReserveVolume(1, true), "admin cannot reserve beyond a completed list's capacity");
        list.ReservedVolume = 98;
        list.Status = SalesListStatus.Closed;
        check(!list.CanReserveVolume(1) && VolumeButtons().Length == 0,
            "closed list cannot be reserved through the small-remainder exception");
        check(!list.CanReserveVolume(1, true), "admin minimum exemption cannot reopen a closed list");
    }
}
