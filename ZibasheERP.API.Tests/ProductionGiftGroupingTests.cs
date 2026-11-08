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
        var mixed=SalesListProductionGrouping.Build(gifts.Append(fancy));
        check(mixed.Count==1&&mixed.Single().VolumeMl==4&&mixed.Single().PreparationNotes?.Contains("فانتزی gold")==true,
            "one recipient has one total even with different bottles and packing instructions remain visible");
        var customLabel = Gift("custom-donor"); customLabel.LabelIdentityText = "Custom name";
        check(SalesListProductionGrouping.Build(gifts.Append(customLabel)) is [var custom]&&custom.VolumeMl==4&&custom.PreparationNotes?.Contains("Custom name")==true,
            "different label instructions do not split the recipient total and remain visible for preparation");
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
            TelegramUsername = "recipient", VolumeMl = 5.0m, Status = SalesListRequestStatus.Confirmed };
        var ownAndGifts=gifts.Append(ownPurchase).ToArray();
        var unchanged=System.Text.Json.JsonSerializer.Serialize(ownAndGifts);
        check(SalesListProductionGrouping.Build(ownAndGifts) is [var total]&&total.VolumeMl==8&&total.SourceCount==4&&total.Request==ownPurchase,
            "the receiver's own purchase joins all gifts in one physical portion using their own bottle and label");
        list.Requests=ownAndGifts;
        decant=((string Main,string? Continuation))channelFormatter.Invoke(null,[list,ownAndGifts,true])!;
        check((decant.Main+decant.Continuation).Contains("8 ml:")&&!(decant.Main+decant.Continuation).Contains("1 ml:")&&!(decant.Main+decant.Continuation).Contains("5 ml:"),
            "live decant output shows only the sum of the receiver's own item and gifts");
        check(Format("FormatOrderList").Contains("8 ml:")&&!Format("FormatOrderList").Contains("8.0 ml:")&&Format("FormatLabelList").Contains("8 ml:"),
            "archived decant and label rosters share the same whole-ml recipient total");
        pdfEntries=(IReadOnlyCollection<PerfumeLabelEntry>)typeof(TelegramWebhookController).GetMethod("BuildPerfumeLabelEntries",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[list])!;
        check(pdfEntries.Count==1&&pdfEntries.Single().VolumeMl==8,"printed PDF data contains exactly one label with own-plus-gifts total");
        channel=((string Main,string? Continuation))channelFormatter.Invoke(null,[list,ownAndGifts,false])!;
        check((channel.Main+channel.Continuation).Contains("5 ml:")&&(channel.Main+channel.Continuation).Contains("1 ml:"),"customer sales posts retain separate purchased quantities and donor entries");
        check(System.Text.Json.JsonSerializer.Serialize(ownAndGifts)==unchanged,"all production and print renderers leave source volumes, bottle choices, gift recipients and prices unchanged");
        var imported=Gift("imported-donor");imported.GiftRecipientTelegramUserId="imported:recipient";
        var adminAlias=Gift("admin-donor",null);adminAlias.GiftRecipientTelegramUserId="admin-username:recipient";
        ownPurchase.TelegramUserId="123456";
        check(SalesListProductionGrouping.Build([ownPurchase,imported,adminAlias,byIdOnly]).Single().VolumeMl==8,"imported, admin username, numeric-only and own identities resolve to one known recipient");
        var renamed=Gift("renamed-donor","new_recipient");renamed.GiftRecipientTelegramUserId="123456";
        check(SalesListProductionGrouping.Build([ownPurchase,renamed]).Single().VolumeMl==6,"a stable Telegram ID joins a renamed recipient to their own item");
        var conflict=Gift("conflicting-donor","recipient");conflict.GiftRecipientTelegramUserId="999999";
        var disputed=SalesListProductionGrouping.Build([ownPurchase,conflict]);
        check(disputed.Count==2&&disputed.Sum(r=>r.VolumeMl)==6&&disputed.All(r=>r.PreparationNotes?.Contains("گیرنده نیازمند بررسی")==true),"conflicting numeric identities never merge merely because usernames match");
        var unknownOne=Gift("unknown-id-one",null);unknownOne.GiftRecipientTelegramUserId="imported:";
        var unknownTwo=Gift("unknown-id-two",null);unknownTwo.GiftRecipientTelegramUserId="imported:";
        check(SalesListProductionGrouping.Build([unknownOne,unknownTwo]).Count==2,"empty imported placeholders do not combine unidentified recipients");
        var secondOwn=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId="123456",TelegramUsername="recipient",VolumeMl=2,Status=SalesListRequestStatus.Confirmed};
        check(SalesListProductionGrouping.Build([ownPurchase,secondOwn]).Count==2,"ordinary multiple purchases without gifts keep their established production behavior");
        check(SalesListProductionGrouping.Build([ownPurchase,secondOwn,imported]).Single().VolumeMl==8,"when gifts exist all of that receiver's own portions are included in the same total");
        var noted=Gift("noted-donor");noted.AdminNotes="شیشه لویی یا دیپتیک";ownPurchase.AdminNotes="بنفش";
        var notedRow=SalesListProductionGrouping.Build([ownPurchase,noted]).Single();
        check(notedRow.VolumeMl==6&&notedRow.PreparationNotes?.Contains("بنفش")==true&&notedRow.PreparationNotes.Contains("لویی یا دیپتیک"),"merging recipient volume preserves all donor and receiver preparation comments for staff");
        var otherCycle=Gift("cycle-donor");otherCycle.SalesListId=Guid.NewGuid();
        check(SalesListProductionGrouping.Build([ownPurchase,imported,otherCycle]).Count==2,"the same recipient in a different list cycle gets a separate total");
        var cancelled = Gift("cancelled"); cancelled.Status = SalesListRequestStatus.Cancelled;
        var deleted = Gift("deleted"); deleted.IsDeleted = true;
        var nextBottle = Gift("next-bottle"); nextBottle.Kind = SalesListRequestKind.NextBottle;
        check(SalesListProductionGrouping.Build(gifts.Concat([cancelled, deleted, nextBottle])).Single().VolumeMl == 3,
            "cancelled, deleted and next-bottle requests do not inflate production volume");
        foreach(var permutation in new[]{ownAndGifts,ownAndGifts.Reverse().ToArray()})
            check(SalesListProductionGrouping.Build(permutation).Sum(r=>r.VolumeMl)==permutation.Sum(r=>r.VolumeMl),"recipient grouping conserves every milliliter independent of request order");
    }
}
