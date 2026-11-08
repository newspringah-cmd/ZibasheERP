using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class InvoiceRestockTracking
{
    public const string Channel="InvoiceRestockTrackingRefresh";
    public const string Caption="ارسال شده به کانال موجودی";
    public static NotificationOutbox RefreshJob(Invoice invoice,Guid customerId,DateTime now)=>new()
    {
        Id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("invoice-restock-report:"+invoice.Id))[..16]),
        CustomerId=customerId,OrderId=invoice.OrderId,Channel=Channel,EventType="RefreshRestockedInvoiceReport",
        Recipient=invoice.Id.ToString(),Payload="{}",CreatedAt=now
    };

    public static async Task<HashSet<Guid>> PublishedInvoiceIdsAsync(AppDbContext db,IReadOnlyCollection<Guid> orderIds,CancellationToken ct=default)
    {
        if(orderIds.Count==0)return [];
        var invoices=await db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&orderIds.Contains(i.OrderId)&&
            i.Status==InvoiceStatus.Cancelled&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status==OrderStatus.Cancelled)
            .Select(i=>new{i.Id,i.OrderId,i.TotalAmount}).ToArrayAsync(ct);
        if(invoices.Length==0)return [];
        var receipts=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="InvoiceCancellationReceipt"&&
            n.EventType=="InvoiceCancelledAndRestocked"&&n.Status==NotificationOutboxStatus.Processed&&n.OrderId!=null&&orderIds.Contains(n.OrderId.Value)).ToArrayAsync(ct);
        var results=receipts.Select(n=>(Row:n,Result:JsonSerializer.Deserialize<InvoiceCancellationService.Result>(n.Payload)!))
            .Where(x=>x.Result.Returned&&x.Result.Offers.Length>0&&x.Result.Offers.Distinct().Count()==x.Result.Offers.Length&&
                invoices.Any(i=>i.Id==x.Result.InvoiceId&&i.OrderId==x.Row.OrderId&&i.TotalAmount==x.Result.ReversedAmount)).ToArray();
        if(results.Length==0)return [];
        var offerIds=results.SelectMany(x=>x.Result.Offers).Distinct().ToArray();
        var offers=await db.SalesLists.AsNoTracking().Where(l=>offerIds.Contains(l.Id))
            .Select(l=>new{l.Id,l.SourceOrderItemId,l.TelegramMessageId,l.TelegramChannelId}).ToArrayAsync(ct);
        // Sold inventory posts are removed and their current message ID cleared.
        // Successful publication and completed report jobs remain durable proof.
        var sentOffers=(await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&offerIds.Contains(n.Id)&&
            n.Channel=="MarketingPromotion"&&n.EventType=="ReadyInventoryPublication"&&n.Status==NotificationOutboxStatus.Processed)
            .Select(n=>n.Id).ToArrayAsync(ct)).ToHashSet();
        var completedReports=(await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==Channel&&
            n.Status==NotificationOutboxStatus.Processed&&n.OrderId!=null&&orderIds.Contains(n.OrderId.Value))
            .Select(n=>n.OrderId!.Value).ToArrayAsync(ct)).ToHashSet();
        var items=await db.OrderItems.AsNoTracking().Where(x=>orderIds.Contains(x.OrderId)&&x.IsDeleted).Select(x=>new{x.Id,x.OrderId}).ToArrayAsync(ct);
        var published=new HashSet<Guid>();
        foreach(var (row,result) in results)
        {
            var stock=offers.Where(l=>result.Offers.Contains(l.Id)).ToArray();
            if(stock.Length==result.Offers.Length&&stock.All(l=>l.SourceOrderItemId.HasValue&&
                (l.TelegramMessageId is >0&&!string.IsNullOrWhiteSpace(l.TelegramChannelId)||sentOffers.Contains(l.Id)||completedReports.Contains(row.OrderId!.Value)))&&
                stock.Select(l=>l.SourceOrderItemId!.Value).ToHashSet().SetEquals(items.Where(x=>x.OrderId==row.OrderId).Select(x=>x.Id)))
                published.Add(result.InvoiceId);
        }
        return published;
    }
}
