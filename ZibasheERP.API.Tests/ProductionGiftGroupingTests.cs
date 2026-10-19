using System.Reflection;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.PerfumeLabels;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

public static class ProductionGiftGroupingTests
{
    public static void Run(Action<bool, string> check)
    {
        var list = new SalesList { Id = Guid.NewGuid(), PublicCode = 123, EnglishName = "Test perfume", TotalVolume = 30 };
        var sequence = 0;
        SalesListRequest Gift(string sender, string? recipient = "recipient", int volume = 1) => new()
        {
            Id = Guid.NewGuid(), SalesListId = list.Id, TelegramUserId = "sender-" + sender,
            TelegramUsername = sender, IsGift = true, GiftRecipientTelegramUsername = recipient,
            VolumeMl = volume, PerfumePricePerMl = 100, BottlePrice = 30,
            Status = SalesListRequestStatus.Confirmed, Kind = SalesListRequestKind.CurrentBottle,
            ConfirmedAt = new DateTime(2026, 10, 1).AddSeconds(++sequence), Bottle = new Bottle { Type = BottleType.Normal }
        };
        var gifts = new[] { Gift("first"), Gift("second", "@RECIPIENT"), Gift("third", " recipient ") };
        list.Requests = gifts;
        var combined = SalesListProductionGrouping.Build(gifts);
        check(combined.Count == 1 && combined[0].VolumeMl == 3 && combined[0].SourceCount == 3,
            "three donors' one-ml gifts produce one three-ml portion for a non-owner");
        check(gifts.All(gift => gift.VolumeMl == 1 && gift.BottlePrice == 30 && gift.PerfumePricePerMl == 100 &&
                gift.Status == SalesListRequestStatus.Confirmed), "production grouping preserves source reservations and billing");

        var channelFormatter = typeof(TelegramWebhookController).GetMethod("FormatChannelSalesListPages", BindingFlags.Static | BindingFlags.NonPublic)!;
        var decant = ((string Main, string? Continuation))channelFormatter.Invoke(null, [list, gifts, true])!;
        check((decant.Main + decant.Continuation).Contains("3 ml:") && !decant.Main.Contains("1 ml:") &&
                decant.Main.Contains("@recipient") && !decant.Main.Contains("@first"),
            "decant output shows recipient once under the combined volume");
        var channel = ((string Main, string? Continuation))channelFormatter.Invoke(null, [list, gifts, false])!;
        check(channel.Main.Contains("1 ml:") && channel.Main.Contains("@first") && channel.Main.Contains("@second"),
            "public sales post retains individual donor reservations");

        string Format(string method) => (string)typeof(InvoiceIssuanceService)
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list])!;
        var labels = Format("FormatLabelList");
        check(labels.Contains("3 ml:") && !labels.Contains("1 ml:") &&
                labels.Split("@recipient").Length == 2, "label print roster contains one recipient entry of three ml");
        check(Format("FormatOrderList").Contains("3 ml:"), "archived decant copy uses the same grouped portions");
        var pdfEntries = (IReadOnlyCollection<PerfumeLabelEntry>)typeof(TelegramWebhookController)
            .GetMethod("BuildPerfumeLabelEntries", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list])!;
        check(pdfEntries.Count == 1 && pdfEntries.Single().VolumeMl == 3, "PDF prints one three-ml label rather than three one-ml labels");

        var owner = new SalesListRequest { Id = Guid.NewGuid(), SalesListId = list.Id, TelegramUserId = "owner-id",
            TelegramUsername = "owner", IsBottleOwner = true, VolumeMl = 10, Status = SalesListRequestStatus.Confirmed };
        var ownerGift = Gift("owner-donor", "@OWNER", 2);
        combined = SalesListProductionGrouping.Build(gifts.Concat([owner, ownerGift]));
        check(combined.Count == 2 && combined.Single(row => row.Request.IsBottleOwner).VolumeMl == 12 &&
                combined.Single(row => !row.Request.IsBottleOwner).VolumeMl == 3,
            "owner gifts still join the owner while other recipient gifts form their own portion");
        var differentRecipient = Gift("fourth", "someoneelse");
        check(SalesListProductionGrouping.Build(gifts.Append(differentRecipient)).Count == 2,
            "gifts to different recipients stay separate");
        var fancy = Gift("fancy-donor"); fancy.Bottle = new Bottle { Type = BottleType.Fancy, Name = "gold" };
        check(SalesListProductionGrouping.Build(gifts.Append(fancy)).Count == 2,
            "normal and fancy bottles are not merged");
        var customLabel = Gift("custom-donor"); customLabel.LabelIdentityText = "Custom name";
        check(SalesListProductionGrouping.Build(gifts.Append(customLabel)).Count == 2,
            "different custom label instructions stay separate");
        var anotherList = Gift("other-cycle"); anotherList.SalesListId = Guid.NewGuid();
        check(SalesListProductionGrouping.Build(gifts.Append(anotherList)).Count == 2,
            "different sales list cycles are never merged");
        check(SalesListProductionGrouping.Build([Gift("unknown-one", null), Gift("unknown-two", null)]).Count == 2,
            "unidentified gift recipients are never combined");
        var byIdAndName = Gift("known-id"); byIdAndName.GiftRecipientTelegramUserId = "123456";
        var byIdOnly = Gift("id-only", null); byIdOnly.GiftRecipientTelegramUserId = "123456";
        check(SalesListProductionGrouping.Build([gifts[0], byIdAndName, byIdOnly]).Single().VolumeMl == 3,
            "known Telegram ID links ID-only and username-only gift entries");
        var ownPurchase = new SalesListRequest { Id = Guid.NewGuid(), SalesListId = list.Id,
            TelegramUsername = "recipient", VolumeMl = 5, Status = SalesListRequestStatus.Confirmed };
        check(SalesListProductionGrouping.Build(gifts.Append(ownPurchase)).Count == 2,
            "ordinary purchases remain separate from grouped gifts");
        var cancelled = Gift("cancelled"); cancelled.Status = SalesListRequestStatus.Cancelled;
        var deleted = Gift("deleted"); deleted.IsDeleted = true;
        var nextBottle = Gift("next-bottle"); nextBottle.Kind = SalesListRequestKind.NextBottle;
        check(SalesListProductionGrouping.Build(gifts.Concat([cancelled, deleted, nextBottle])).Single().VolumeMl == 3,
            "cancelled, deleted and next-bottle requests do not inflate production volume");
    }
}
