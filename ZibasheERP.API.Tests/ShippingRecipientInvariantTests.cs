using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;

public static class ShippingRecipientInvariantTests
{
    public static void Run(Action<bool,string> check)
    {
        var profiles=Enumerable.Range(0,6).Select(n=>new Customer{Id=Guid.NewGuid(),TelegramId=(881100+n).ToString(),Username="recipient_"+n}).ToArray();
        var rows=new List<OrderItem>();var expected=new Dictionary<Guid,Guid?>();
        var notifications=Array.Empty<NotificationOutbox>().AsQueryable();
        foreach(var payer in profiles)
        foreach(var recipient in profiles)
        foreach(var mode in Enumerable.Range(0,6))
        {
            var conflicting=profiles.First(p=>p.Id!=recipient.Id);
            var gift=new SalesListRequest{Id=Guid.NewGuid(),IsGift=true,
                GiftRecipientTelegramUserId=mode switch{0=>recipient.TelegramId,2=>recipient.TelegramId,3=>"999999999",4=>"imported:"+recipient.Username,_=>null},
                GiftRecipientTelegramUsername=mode switch{2=>conflicting.Username,5=>null,_=>" @"+recipient.Username!.ToUpperInvariant()+" "}};
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=Guid.NewGuid(),Order=new Order{CustomerId=payer.Id,IsInventory=mode%2==0},SourceSalesListRequest=gift};
            rows.Add(item);expected[item.Id]=mode is 2 or 3 or 5?null:recipient.Id;
        }
        var matches=rows.ToDictionary(r=>r.Id,_=>new List<Guid>());
        foreach(var profile in profiles)
            foreach(var item in ShippingRecipientPolicy.ItemsForCustomer(rows.AsQueryable(),notifications,profile.Id,profile.TelegramId,profile.Username,profiles.AsQueryable()))
                matches[item.Id].Add(profile.Id);
        check(rows.Count==216&&matches.All(x=>x.Value.Count<=1),"recipient invariant: 216 ordinary/inventory, payer and recipient combinations never assign one gift to two customers");
        check(matches.All(x=>expected[x.Key].HasValue?x.Value.SequenceEqual(new[]{expected[x.Key]!.Value}):x.Value.Count==0),
            "recipient invariant: valid gifts belong exactly to their receiver; conflicting IDs, wrong stable IDs and missing identities belong to nobody");
        check(rows.Where(r=>expected[r.Id]!=r.Order!.CustomerId).All(r=>!matches[r.Id].Contains(r.Order!.CustomerId)),
            "recipient invariant: outgoing gifts cannot reappear under their payer through any inventory or username fallback");
        check(profiles.All(profile=>ShippingRecipientPolicy.GiftRequestsForCustomer(rows.Select(r=>r.SourceSalesListRequest!).AsQueryable(),
                profiles.AsQueryable(),profile.Id,profile.TelegramId,profile.Username).Select(r=>r.Id).ToHashSet()
            .SetEquals(rows.Where(r=>matches[r.Id].Contains(profile.Id)).Select(r=>r.SourceSalesListRequest!.Id))),
            "recipient invariant: pending-request fallback and billed-item reports use exactly the same gift identity rule across all 216 cases");
        var giftWithOldName=rows.First(r=>expected[r.Id].HasValue&&r.SourceSalesListRequest!.GiftRecipientTelegramUserId==profiles[1].TelegramId);
        giftWithOldName.SourceSalesListRequest!.GiftRecipientTelegramUsername="no_longer_used_name";
        check(ShippingRecipientPolicy.ItemsForCustomer(new[]{giftWithOldName}.AsQueryable(),notifications,profiles[1].Id,profiles[1].TelegramId,profiles[1].Username,profiles.AsQueryable()).Any(),
            "stable recipient ID keeps a gift with its receiver after an unclaimed username change");
        giftWithOldName.SourceSalesListRequest.GiftRecipientTelegramUsername=profiles[2].Username;
        check(!ShippingRecipientPolicy.ItemsForCustomer(new[]{giftWithOldName}.AsQueryable(),notifications,profiles[1].Id,profiles[1].TelegramId,profiles[1].Username,profiles.AsQueryable()).Any()&&
            !ShippingRecipientPolicy.ItemsForCustomer(new[]{giftWithOldName}.AsQueryable(),notifications,profiles[2].Id,profiles[2].TelegramId,profiles[2].Username,profiles.AsQueryable()).Any(),
            "username reuse cannot move a stable-ID gift or count it for two accounts; manual review is required");
    }
}
