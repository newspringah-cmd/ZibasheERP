using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static partial class InvoiceRestockTracking
{
    public const string DeferredChannel = "InvoiceDeferredRestock";
    public const string DecantedChannel = "SalesListDecantCompletion";
    public const string WaitingCaption = "فاکتور حذف‌شده؛ در انتظار آماده‌سازی و انتقال به موجودی";
    public const string PaidCaption = "موجودی برگشتی فروخته و توسط مشتری جدید پرداخت شده است";
    public sealed record ItemState(string Icon,string Note,bool Waiting=false,bool InInventory=false,bool Published=false,bool Paid=false);
    private sealed record Stock(Guid Id,Guid Source,decimal Volume,bool Posted);
    private sealed record Sale(Guid Id,Guid Stock,decimal Volume,bool Paid,bool Cancelled,bool Active);

    public static ItemState Describe(IEnumerable<ItemState> source)
    {
        var states=source.ToArray();
        if(states.Length>0&&states.All(s=>s.Paid))return new("🟢",PaidCaption,Paid:true,InInventory:true,Published:true);
        if(states.Any(s=>s.Waiting))return new("🟣",WaitingCaption,Waiting:true);
        if(states.Length>0&&states.All(s=>s.Published||s.Paid))return new("🔵",Caption,InInventory:true,Published:true);
        return new("🔵",UnknownCaption,InInventory:states.Any(s=>s.InInventory));
    }

    public static async Task<bool> IsReadyAsync(AppDbContext db,OrderItem item,CancellationToken ct=default)
    {
        if(item.ShippedAt!=null||item.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped)return false;
        if(item.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip)return true;
        if(item.SalesListId is not Guid list)return false;
        if(await db.OrderItems.AsNoTracking().AnyAsync(i=>!i.IsDeleted&&i.SalesListId==list&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&
            (i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip||i.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped),ct))return true;
        if(await db.NotificationOutbox.AsNoTracking().AnyAsync(n=>!n.IsDeleted&&n.Channel==DecantedChannel&&n.Recipient==list.ToString("N")&&n.Status==NotificationOutboxStatus.Processed,ct))return true;
        var legacy=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="Telegram"&&n.EventType=="DecantPhotoDelivery"&&n.Payload.Contains(list.ToString())).Select(n=>n.Payload).ToArrayAsync(ct);
        return legacy.Any(payload=>{using var json=JsonDocument.Parse(payload);return json.RootElement.TryGetProperty("SalesListId",out var id)&&id.TryGetGuid(out var value)&&value==list;});
    }

    public static async Task<Dictionary<Guid,ItemState>> ItemStatesAsync(AppDbContext db,IReadOnlyCollection<Guid> orderIds,CancellationToken ct=default)
    {
        if(orderIds.Count==0)return [];
        var items=await db.OrderItems.AsNoTracking().Where(i=>orderIds.Contains(i.OrderId)&&i.IsDeleted).ToArrayAsync(ct);
        var receipts=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.OrderId!=null&&orderIds.Contains(n.OrderId.Value)&&n.Channel=="InvoiceCancellationReceipt"&&n.Status==NotificationOutboxStatus.Processed).Select(n=>n.Payload).ToArrayAsync(ct);
        var waiting=receipts.Select(p=>JsonSerializer.Deserialize<InvoiceCancellationService.Result>(p)!).SelectMany(r=>r.WaitingItemIds??[]).ToHashSet();
        var stock=new Dictionary<Guid,Stock>();var sales=new Dictionary<Guid,Sale>();var pending=items.Select(i=>i.Id).ToArray();
        for(var depth=0;depth<32&&pending.Length>0;depth++)
        {
            var found=await db.SalesLists.AsNoTracking().Where(l=>l.SourceOrderItemId!=null&&pending.Contains(l.SourceOrderItemId.Value))
                .Select(l=>new Stock(l.Id,l.SourceOrderItemId!.Value,l.TotalVolume,l.TelegramMessageId>0&&l.TelegramChannelId!=null)).ToArrayAsync(ct);
            var fresh=found.Where(s=>!stock.ContainsKey(s.Id)).ToArray();foreach(var offer in fresh)stock.Add(offer.Id,offer);
            var ids=fresh.Select(s=>s.Id).ToArray();if(ids.Length==0)break;
            var buyers=await db.OrderItems.AsNoTracking().Where(i=>i.SalesListId!=null&&ids.Contains(i.SalesListId.Value)&&i.Order!=null&&!i.Order.IsDeleted)
                .Select(i=>new Sale(i.Id,i.SalesListId!.Value,i.RequestedVolumeMl,
                    !i.IsDeleted&&i.Order!.Status!=OrderStatus.Cancelled&&i.Order.Invoices.Count(v=>!v.IsDeleted&&v.Status!=InvoiceStatus.Cancelled&&v.Status!=InvoiceStatus.Draft)==1&&i.Order.Invoices.Any(v=>!v.IsDeleted&&v.Status==InvoiceStatus.Paid),
                    i.Order!.Status==OrderStatus.Cancelled&&i.Order.Invoices.Any(v=>!v.IsDeleted&&v.Status==InvoiceStatus.Cancelled),
                    !i.IsDeleted&&i.Order!.Status!=OrderStatus.Cancelled)).ToArrayAsync(ct);
            foreach(var sale in buyers)sales[sale.Id]=sale;
            pending=buyers.Where(s=>s.Cancelled).Select(s=>s.Id).ToArray();
        }
        var stockIds=stock.Keys.ToArray();
        var published=(await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&stockIds.Contains(n.Id)&&n.Channel=="MarketingPromotion"&&n.EventType=="ReadyInventoryPublication"&&n.Status==NotificationOutboxStatus.Processed).Select(n=>n.Id).ToArrayAsync(ct)).ToHashSet();
        bool SoldPaid(Guid source,HashSet<Guid> seen)
        {
            if(!seen.Add(source))return false;
            var offers=stock.Values.Where(s=>s.Source==source).ToArray();if(offers.Length!=1)return false;
            var offer=offers[0];var buyers=sales.Values.Where(s=>s.Stock==offer.Id).ToArray();var active=buyers.Where(s=>s.Active).ToArray();
            if(active.Length>0)return active.Length==1&&active[0].Paid&&active[0].Volume>=offer.Volume;
            var cancelled=buyers.Where(s=>s.Cancelled).ToArray();
            return cancelled.Length==1&&SoldPaid(cancelled[0].Id,new(seen));
        }
        return items.ToDictionary(i=>i.Id,i=>
        {
            var offers=stock.Values.Where(s=>s.Source==i.Id).ToArray();
            if(SoldPaid(i.Id,[]))return new ItemState("🟢",PaidCaption,InInventory:true,Published:true,Paid:true);
            if(offers.Length>0)
            {
                var posted=offers.All(s=>s.Posted||published.Contains(s.Id));
                return new ItemState("🔵",posted?Caption:UnknownCaption,InInventory:true,Published:posted);
            }
            if(waiting.Contains(i.Id)||i.FulfillmentStatus is OrderItemFulfillmentStatus.WaitingForArrivalInIran or OrderItemFulfillmentStatus.DecantQueue)
                return new ItemState("🟣",WaitingCaption,Waiting:true);
            return new ItemState("🔵",UnknownCaption);
        });
    }

    public static async Task ReconcileAsync(AppDbContext db,CancellationToken ct=default)
    {
        var jobs=await db.NotificationOutbox.Where(n=>!n.IsDeleted&&n.Channel==Channel&&n.OrderId!=null&&n.Status!=NotificationOutboxStatus.Processing).ToArrayAsync(ct);
        var orders=jobs.Select(n=>n.OrderId!.Value).Distinct().ToArray();if(orders.Length==0)return;
        var states=await ItemStatesAsync(db,orders,ct);
        var items=await db.OrderItems.AsNoTracking().Where(i=>orders.Contains(i.OrderId)&&i.IsDeleted).Select(i=>new{i.Id,i.OrderId}).ToArrayAsync(ct);
        foreach(var job in jobs)
        {
            var data=items.Where(i=>i.OrderId==job.OrderId&&states.ContainsKey(i.Id)).OrderBy(i=>i.Id).Select(i=>new{i.Id,State=states[i.Id]});
            var payload=JsonSerializer.Serialize(new{Signature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data))))});
            if(job.Payload==payload)continue;
            job.Payload=payload;job.Status=NotificationOutboxStatus.Pending;job.NextAttemptAt=null;job.LockedUntil=null;job.ProcessedAt=null;job.UpdatedAt=DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
