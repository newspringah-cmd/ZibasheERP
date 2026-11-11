using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Tracking;

public sealed record PreparedTrackingAddress(Guid ReferenceId,Guid CustomerId,Guid AddressId,Guid? RequestId,
    string ReceiverName,string City,string FullAddress,string CustomerName,string Username,long MessageId,DateTime PostedAt);

public static class TrackingPreparedAddresses
{
    private sealed record Root(Guid CustomerId,Guid? RequestId,Guid[] ItemIds,string ChatId,string Kind,
        long? MessageId,bool Installed,Guid? AddressId);

    public static Task<PreparedTrackingAddress[]> LoadAsync(AppDbContext db,string shippingChat,CancellationToken ct,DateTime? sourceAsOf=null)=>LoadCoreAsync(db,shippingChat,ct,sourceAsOf,false);
    public static Task<PreparedTrackingAddress[]> LoadForNameWarningsAsync(AppDbContext db,string shippingChat,CancellationToken ct)=>LoadCoreAsync(db,shippingChat,ct,null,true);
    private static async Task<PreparedTrackingAddress[]> LoadCoreAsync(AppDbContext db,string shippingChat,CancellationToken ct,DateTime? sourceAsOf,bool forNameWarnings)
    {
        if(string.IsNullOrWhiteSpace(shippingChat)||!shippingChat.StartsWith('-'))return [];
        var asOf=sourceAsOf??DateTime.UtcNow;var since=forNameWarnings?DateTime.MinValue:asOf.AddDays(-45);
        var rows=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="ShippingReferenceContext"&&
            n.EventType=="AddressReferences"&&n.Recipient==shippingChat&&n.CreatedAt>=since&&n.CreatedAt<=asOf).ToArrayAsync(ct);
        var roots=new List<(NotificationOutbox Row,Root Data)>();
        foreach(var row in rows)
        {
            Root? r;try{r=JsonSerializer.Deserialize<Root>(row.Payload);}catch(JsonException){continue;}
            if(r is not null&&r.Kind=="root"&&r.Installed&&r.CustomerId==row.CustomerId&&r.ChatId==shippingChat&&r.MessageId>0&&r.AddressId.HasValue&&r.ItemIds is not null)
                roots.Add((row,r));
        }
        var addressIds=roots.Select(r=>r.Data.AddressId!.Value).Distinct().ToArray();
        var addresses=await db.Addresses.AsNoTracking().Include(a=>a.Customer).Where(a=>!a.IsDeleted&&(a.UpdatedAt??a.CreatedAt)<=asOf&&addressIds.Contains(a.Id)&&a.Customer!=null&&!a.Customer.IsDeleted&&
            (forNameWarnings||db.CustomerTelegramGroups.Any(g=>g.CustomerId==a.CustomerId&&!g.IsDeleted&&g.IsActive))).ToDictionaryAsync(a=>a.Id,ct);
        var ids=roots.SelectMany(r=>r.Data.ItemIds).Distinct().ToArray();
        var ready=(await db.OrderItems.AsNoTracking().Where(i=>ids.Contains(i.Id)&&!i.IsDeleted&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null)
            .Select(i=>i.Id).ToArrayAsync(ct)).ToHashSet();
        var requestIds=roots.Where(r=>r.Data.RequestId.HasValue).Select(r=>r.Data.RequestId!.Value).Distinct().ToArray();
        var open=(await db.OrderItems.AsNoTracking().Where(i=>i.ShippingRequestId!=null&&requestIds.Contains(i.ShippingRequestId.Value)&&!i.IsDeleted&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null&&
            !db.OrderItems.Any(other=>other.ShippingRequestId==i.ShippingRequestId&&!other.IsDeleted&&(other.ShippedAt!=null||other.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped)))
            .Select(i=>i.ShippingRequestId!.Value).Distinct().ToArrayAsync(ct)).ToHashSet();
        // Explicit legacy shared parcels are real address scopes even when their contents predate ERP invoices.
        var sharedIds=requestIds.Select(SharedShippingService.ReceiptId).ToArray();
        var sharedRows=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==SharedShippingService.Channel&&sharedIds.Contains(n.Id)&&(n.UpdatedAt??n.CreatedAt)<=asOf).ToArrayAsync(ct);
        foreach(var row in sharedRows)
        {
            SharedShippingService.Parcel? parcel;try{parcel=JsonSerializer.Deserialize<SharedShippingService.Parcel>(row.Payload);}catch(JsonException){continue;}
            if(parcel is null||parcel.ClosedAt!=null||parcel.CreatedAt>asOf||row.Id!=SharedShippingService.ReceiptId(parcel.RequestId)||row.CustomerId!=parcel.PrimaryCustomerId||
                !roots.Any(r=>r.Data.RequestId==parcel.RequestId&&r.Data.CustomerId==parcel.PrimaryCustomerId&&r.Data.AddressId==parcel.AddressId))continue;
            if(await SharedShippingService.Items(db,parcel.RequestId).AsNoTracking().AnyAsync(ct))continue;
            try{if(await SharedShippingService.ReadAsync(db,parcel.RequestId,ct)is not null)open.Add(parcel.RequestId);}
            catch(InvalidOperationException){ /* Invalid receipt metadata cannot grant a tracking destination. */ }
        }
        var customers=roots.Select(r=>r.Data.CustomerId).Distinct().ToArray();
        var sent=await db.TrackingDispatches.AsNoTracking().Where(d=>customers.Contains(d.CustomerId!.Value)&&!d.IsDeleted&&d.Status==TrackingDispatchStatus.Sent&&d.SentAt>=since)
            .Select(d=>new{d.CustomerId,d.ShippingRequestId,d.SentAt,SourceAsOf=d.ImportBatch!.CreatedAt}).ToArrayAsync(ct);
        var deliveredSources=new HashSet<long>();
        if(forNameWarnings)
        {
            var notices=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ZibasheERP.API.Telegram.ShippingTrackingNotices.Channel&&n.Recipient==shippingChat).Select(n=>new{n.CustomerId,n.Payload}).ToArrayAsync(ct);
            foreach(var row in notices)
            {
                try{var data=JsonSerializer.Deserialize<ZibasheERP.API.Telegram.ShippingTrackingNotices.Data>(row.Payload);if(data is not null&&data.CustomerId==row.CustomerId&&data.ChatId==shippingChat&&data.SourceMessageId>0)deliveredSources.Add(data.SourceMessageId);}
                catch(JsonException){ /* Unknown delivery metadata cannot close a source. */ }
            }
        }
        var manuallyClosed=new HashSet<long>();
        var completions=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ZibasheERP.API.Telegram.ManualShippingAddresses.Channel&&n.Recipient==shippingChat&&n.Status==NotificationOutboxStatus.Processed).ToArrayAsync(ct);
        foreach(var completion in completions)
        {
            try
            {
                var d=JsonSerializer.Deserialize<ZibasheERP.API.Telegram.ManualShippingAddresses.Completion>(completion.Payload);
                if(d is not null&&completion.Id==ZibasheERP.API.Telegram.ManualShippingAddresses.Key(d.ReferenceId)&&completion.CustomerId==d.CustomerId&&d.ChatId==shippingChat&&
                    roots.Any(r=>r.Row.Id==d.ReferenceId&&r.Data.CustomerId==d.CustomerId&&r.Data.AddressId==d.AddressId&&r.Data.MessageId==d.MessageId))manuallyClosed.Add(d.MessageId);
            }
            catch(JsonException){ /* Invalid completion metadata cannot close an address. */ }
        }
        var result=new List<PreparedTrackingAddress>();
        foreach(var (row,r) in roots.OrderByDescending(r=>r.Row.CreatedAt))
        {
            if(deliveredSources.Contains(r.MessageId!.Value)||manuallyClosed.Contains(r.MessageId.Value))continue;
            if(!addresses.TryGetValue(r.AddressId!.Value,out var address)||address.CustomerId!=r.CustomerId)continue;
            if(r.RequestId.HasValue?!open.Contains(r.RequestId.Value):r.ItemIds.Length>0&&!r.ItemIds.All(ready.Contains))continue;
            if(sent.Any(d=>d.CustomerId==r.CustomerId&&d.SentAt>=row.CreatedAt&&
                (r.RequestId.HasValue?d.ShippingRequestId==r.RequestId:!forNameWarnings||d.ShippingRequestId is null&&row.CreatedAt<=d.SourceAsOf&&
                    roots.Where(x=>x.Data.CustomerId==r.CustomerId&&x.Data.RequestId is null&&x.Row.CreatedAt<=d.SourceAsOf).Select(x=>x.Data.AddressId).Distinct().Count()==1)))continue;
            result.Add(new(row.Id,r.CustomerId,address.Id,r.RequestId,address.ReceiverName,address.City,address.FullAddress,
                address.Customer!.FullName,address.Customer.Username??"",r.MessageId!.Value,row.CreatedAt));
        }
        return result.DistinctBy(r=>(r.CustomerId,r.AddressId,r.RequestId)).ToArray();
    }
}
