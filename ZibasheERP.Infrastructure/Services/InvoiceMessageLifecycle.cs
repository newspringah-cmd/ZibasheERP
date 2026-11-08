using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class InvoiceMessageLifecycle
{
    public sealed record Receipt(Guid InvoiceId,string ChatId,long MessageId,string Kind,Guid SourceEventId,DateTime? DeletedAtUtc=null);
    public sealed record Cleanup(Guid InvoiceId,string ChatId,long MessageId,Guid? ReceiptId);
    private static Guid Key(string value)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    public static async Task RecordAsync(AppDbContext db,Guid orderId,Guid sourceEventId,string chat,long message,string kind,CancellationToken ct=default)
    {
        if(message<=0||!long.TryParse(chat,out var chatId)||chatId>=0)return;
        var invoices=await db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&i.OrderId==orderId).Select(i=>new{i.Id,i.Status}).ToArrayAsync(ct);
        if(invoices.Length!=1)return; // Never guess between reissued financial documents.
        var invoice=invoices[0];var id=Key($"invoice-message:{invoice.Id}:{chat}:{message}");
        if(!await db.NotificationOutbox.AnyAsync(n=>n.Id==id,ct)&&!db.NotificationOutbox.Local.Any(n=>n.Id==id))
            db.NotificationOutbox.Add(new(){Id=id,OrderId=orderId,Channel="InvoiceMessageReceipt",EventType=kind,Recipient=chat,
                Payload=JsonSerializer.Serialize(new Receipt(invoice.Id,chat,message,kind,sourceEventId)),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});
        if(invoice.Status==InvoiceStatus.Cancelled)await QueueAsync(db,orderId,new(invoice.Id,chat,message,id),ct);
    }
    private static async Task QueueAsync(AppDbContext db,Guid orderId,Cleanup target,CancellationToken ct)
    {
        var id=Key($"invoice-cleanup:{target.InvoiceId}:{target.ChatId}:{target.MessageId}");
        if(await db.NotificationOutbox.AnyAsync(n=>n.Id==id,ct)||db.NotificationOutbox.Local.Any(n=>n.Id==id))return;
        db.NotificationOutbox.Add(new(){Id=id,OrderId=orderId,Channel="InvoiceCancellationCleanup",EventType="DeleteInvoiceBundleMessage",Recipient=target.ChatId,
            Payload=JsonSerializer.Serialize(target),CreatedAt=DateTime.UtcNow});
    }
    public static async Task QueueKnownAsync(AppDbContext db,Invoice invoice,CancellationToken ct=default)
    {
        if(invoice.Status!=InvoiceStatus.Cancelled)throw new InvalidOperationException("Only cancelled invoices can queue message cleanup");
        var receipts=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==invoice.OrderId&&n.Channel=="InvoiceMessageReceipt"&&!n.IsDeleted).ToArrayAsync(ct);
        foreach(var row in receipts)
        {
            var r=JsonSerializer.Deserialize<Receipt>(row.Payload);
            if(r is not null&&r.InvoiceId==invoice.Id&&r.DeletedAtUtc==null)await QueueAsync(db,invoice.OrderId,new(invoice.Id,r.ChatId,r.MessageId,row.Id),ct);
        }
        if(invoice.TelegramInvoiceMessageId is >0&&!string.IsNullOrWhiteSpace(invoice.TelegramInvoiceChatId))
            await QueueAsync(db,invoice.OrderId,new(invoice.Id,invoice.TelegramInvoiceChatId,invoice.TelegramInvoiceMessageId.Value,null),ct);
    }
}
