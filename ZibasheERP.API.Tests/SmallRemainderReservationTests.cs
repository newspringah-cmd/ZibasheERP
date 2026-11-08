using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

public static class SmallRemainderReservationTests
{
    public static async Task RunSqlAsync(Action<bool, string> check)
    {
        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var target = new SqlConnectionStringBuilder(connection);
        if (target.InitialCatalog != "ZibasheERPCloudDev" || target.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Remainder tests require the isolated development database");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        await using var db = new AppDbContext(options);
        var list = new SalesList { Id = Guid.NewGuid(), PerfumeId = await db.Perfumes.Select(v => v.Id).FirstAsync(),
            PublicCode = Random.Shared.Next(2000000, 3000000), EnglishName = "Whole remainder fixture", PersianName = "Fixture",
            TotalVolume = 100, ReservedVolume = 97, MinimumRequestVolumeMl = 5, Status = SalesListStatus.Open,
            OpenDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, Gender = PerfumeGender.Unisex };
        var requests = new[] { 1, 2, 3, 3 }.Select((volume,index) => new SalesListRequest {
            Id = Guid.NewGuid(), SalesListId = list.Id, VolumeMl = volume, TelegramUserId = "fixture_" + list.Id.ToString("N") + index,
            TelegramUsername = "fixture_" + index, ExternalReference = "remainder-" + Guid.NewGuid().ToString("N"),
            IsBottleOwner = true, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), Status = SalesListRequestStatus.PendingConfirmation }).ToArray();
        db.SalesLists.Add(list); db.SalesListRequests.AddRange(requests); await db.SaveChangesAsync();
        async Task<bool> Confirm(SalesListRequest request)
        {
            await using var context = new AppDbContext(options);
            try
            {
                await new SalesListRequestRepository(context).ConfirmCurrentBottleAsync(request.Id, request.TelegramUserId);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("این مقدار قابل ثبت نیست", StringComparison.Ordinal))
            { return false; }
        }
        try
        {
            check(!await Confirm(requests[0]) && !await Confirm(requests[1]),
                "stale pending one-ml and two-ml requests cannot split the final three ml during SQL confirmation");
            db.ChangeTracker.Clear();
            check((await db.SalesLists.AsNoTracking().SingleAsync(v => v.Id == list.Id)).ReservedVolume == 97,
                "rejected partial remainders leave reserved volume unchanged");
            var results = await Task.WhenAll(Confirm(requests[2]), Confirm(requests[3]));
            check(results.Count(v => v) == 1,
                "concurrent attempts to take the whole remainder allow exactly one confirmation");
            var final = await db.SalesLists.AsNoTracking().SingleAsync(v => v.Id == list.Id);
            check(final.ReservedVolume == 100 && final.Status == SalesListStatus.Full && final.MinimumRequestVolumeMl == 5 &&
                await db.SalesListRequests.CountAsync(v => v.SalesListId == list.Id && v.Status == SalesListRequestStatus.Confirmed) == 1,
                "taking the final remainder completes the list without changing its configured minimum or overbooking");
            var winner=await db.SalesListRequests.AsNoTracking().SingleAsync(v=>v.SalesListId==list.Id&&v.Status==SalesListRequestStatus.Confirmed);
            var fractionalEdits=0;
            foreach(var fullEdit in new[]{false,true})
            {
                await using var editDb=new AppDbContext(options);
                try
                {
                    var repository=new SalesListRequestRepository(editDb);
                    if(fullEdit)await repository.UpdateConfirmedCurrentBottleRequestAsync(winner.Id,3,2.5m,null,0);
                    else await repository.UpdateConfirmedVolumeAsync(winner.Id,3,2.5m);
                }
                catch(InvalidOperationException e) when(e.Message.Contains("عدد صحیح")){fractionalEdits++;}
            }
            check(fractionalEdits==2&&(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==list.Id)).ReservedVolume==100,
                "both admin edit paths refuse half-ml volumes for ordinary lists without changing reserved capacity");
        }
        finally
        {
            await db.SalesListRequests.Where(v => v.SalesListId == list.Id).ExecuteDeleteAsync();
            await db.SalesLists.Where(v => v.Id == list.Id).ExecuteDeleteAsync();
        }
    }

    public static void Run(Action<bool, string> check)
    {
        var list = new SalesList { Id = Guid.NewGuid(), TotalVolume = 100, ReservedVolume = 98,
            MinimumRequestVolumeMl = 5, Status = SalesListStatus.Open };
        TelegramInlineButton[] VolumeButtons() => ((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)
            typeof(TelegramWebhookController).GetMethod("BuildChannelVolumeButtons", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [list, null])!).SelectMany(row => row)
            .Where(button => button.CallbackData?.StartsWith("slv:") == true).ToArray();

        check(list.EffectiveMinimumRequestVolumeMl == 2 && !list.CanReserveVolume(1) && list.CanReserveVolume(2),
            "customers must take the whole final remainder when it is below the configured minimum");
        // SQL decimal columns materialize integer amounts with a trailing zero.
        list.TotalVolume=100.0m;list.ReservedVolume=98.0m;list.MinimumRequestVolumeMl=5.0m;
        check(VolumeButtons().Single().Text=="2 ml"&&VolumeButtons().Single().CallbackData!.EndsWith(":2"),
            "ordinary channel buttons keep whole-ml labels and callback values after decimal database materialization");
        var pages=((string Main,string? Continuation))typeof(TelegramWebhookController).GetMethod("FormatChannelSalesListPages",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[list,new[]{new SalesListRequest{VolumeMl=3.0m,Kind=SalesListRequestKind.CurrentBottle,TelegramUsername="fixture"}},false])!;
        check(pages.Main.Contains("100ml")&&pages.Main.Contains("2 میل")&&pages.Main.Contains("3 ml:")&&!pages.Main.Contains("100.0ml")&&!pages.Main.Contains("2.0 میل")&&!pages.Main.Contains("3.0 ml:"),
            "ordinary list caption and customer roster show integer ml without decimal zeros");
        check(!list.CanReserveVolume(1.5m,true)&&!list.CanReserveVolume(0.5m),
            "half-ml registration remains unavailable for ordinary lists including admin entry");
        var buttons = VolumeButtons();
        check(buttons.Select(button => button.Text).SequenceEqual(["2 ml"]) &&
                buttons.All(button => button.CallbackData!.Contains(TelegramCallbackParser.EncodeGuid(list.Id))),
            "small remainder buttons offer only the whole remainder on the correct list cycle");
        check(!list.CanReserveVolume(0) && !list.CanReserveVolume(-1) && !list.CanReserveVolume(3),
            "small-remainder exception never permits zero, negative volume or overbooking");

        list.ReservedVolume = 97;
        check(list.EffectiveMinimumRequestVolumeMl == 3 && !list.CanReserveVolume(1) && !list.CanReserveVolume(2) && list.CanReserveVolume(3) &&
                VolumeButtons().Select(button => button.Text).SequenceEqual(["3 ml"]),
            "five-ml minimum with three ml left offers exactly three ml and blocks splitting it into one plus two");
        list.MinimumRequestVolumeMl = 10; list.ReservedVolume = 94;
        check(VolumeButtons().Single().Text == "6 ml" && list.CanReserveVolume(6) && !list.CanReserveVolume(5),
            "a final remainder outside preset button sizes is offered exactly in full");
        list.MinimumRequestVolumeMl = 50; list.ReservedVolume = 51;
        check(VolumeButtons().Single().Text == "49 ml" && !list.CanReserveVolume(30),
            "large nonstandard final remainders remain selectable without allowing partial requests");
        list.MinimumRequestVolumeMl = 5;
        list.ReservedVolume = 99;
        check(VolumeButtons().Single().Text == "1 ml" && list.CanReserveVolume(1),
            "a naturally remaining single ml can still complete the list");
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
