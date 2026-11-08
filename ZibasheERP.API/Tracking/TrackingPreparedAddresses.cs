using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Tracking;

public sealed record PreparedTrackingAddress(Guid ReferenceId,Guid CustomerId,Guid AddressId,Guid? RequestId,
    string ReceiverName,string City,string FullAddress,string CustomerName,string Username,long MessageId,DateTime PostedAt);

public static class TrackingPreparedAddresses
{
    private sealed record Root(Guid CustomerId,Guid? RequestId,Guid[] ItemIds,string ChatId,string Kind,
        long? MessageId,bool Installed,Guid? AddressId);

    public static async Task<PreparedTrackingAddress[]> LoadAsync(AppDbContext db,string shippingChat,CancellationToken ct,DateTime? sourceAsOf=null)
    {
        if(string.IsNullOrWhiteSpace(shippingChat)||!shippingChat.StartsWith('-'))return [];
        var asOf=sourceAsOf??DateTime.UtcNow;var since=asOf.AddDays(-45);
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
            db.CustomerTelegramGroups.Any(g=>g.CustomerId==a.CustomerId&&!g.IsDeleted&&g.IsActive)).ToDictionaryAsync(a=>a.Id,ct);
        var ids=roots.SelectMany(r=>r.Data.ItemIds).Distinct().ToArray();
        var ready=(await db.OrderItems.AsNoTracking().Where(i=>ids.Contains(i.Id)&&!i.IsDeleted&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null)
            .Select(i=>i.Id).ToArrayAsync(ct)).ToHashSet();
        var requestIds=roots.Where(r=>r.Data.RequestId.HasValue).Select(r=>r.Data.RequestId!.Value).Distinct().ToArray();
        var open=(await db.OrderItems.AsNoTracking().Where(i=>i.ShippingRequestId!=null&&requestIds.Contains(i.ShippingRequestId.Value)&&!i.IsDeleted&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null&&
            !db.OrderItems.Any(other=>other.ShippingRequestId==i.ShippingRequestId&&!other.IsDeleted&&(other.ShippedAt!=null||other.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped)))
            .Select(i=>i.ShippingRequestId!.Value).Distinct().ToArrayAsync(ct)).ToHashSet();
        var customers=roots.Select(r=>r.Data.CustomerId).Distinct().ToArray();
        var sent=await db.TrackingDispatches.AsNoTracking().Where(d=>customers.Contains(d.CustomerId!.Value)&&!d.IsDeleted&&d.Status==TrackingDispatchStatus.Sent&&d.SentAt>=since)
            .Select(d=>new{d.CustomerId,d.ShippingRequestId,d.SentAt}).ToArrayAsync(ct);
        var result=new List<PreparedTrackingAddress>();
        foreach(var (row,r) in roots.OrderByDescending(r=>r.Row.CreatedAt))
        {
            if(!addresses.TryGetValue(r.AddressId!.Value,out var address)||address.CustomerId!=r.CustomerId)continue;
            if(r.RequestId.HasValue?!open.Contains(r.RequestId.Value):r.ItemIds.Length>0&&!r.ItemIds.All(ready.Contains))continue;
            if(sent.Any(d=>d.CustomerId==r.CustomerId&&d.SentAt>=row.CreatedAt&&(!r.RequestId.HasValue||d.ShippingRequestId==r.RequestId)))continue;
            result.Add(new(row.Id,r.CustomerId,address.Id,r.RequestId,address.ReceiverName,address.City,address.FullAddress,
                address.Customer!.FullName,address.Customer.Username??"",r.MessageId!.Value,row.CreatedAt));
        }
        return result.DistinctBy(r=>(r.CustomerId,r.AddressId,r.RequestId)).ToArray();
    }
}
