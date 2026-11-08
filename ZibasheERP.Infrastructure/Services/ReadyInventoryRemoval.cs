using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class ReadyInventoryRemoval
{
    public const string RetirementChannel="InventoryPostRetirement";
    public const string ManualAlertEvent="InventoryPostCleanupNeedsManualAction";
    public static string Binding(Guid listId,string chat,long message)=>$"{listId}:{chat}:{message}";

    public static async Task<bool> QueueManualAlertAsync(AppDbContext db,Guid listId,string failureChat,
        bool withdrawn,bool restricted,CancellationToken ct=default)
    {
        if(!long.TryParse(failureChat.Trim(),out var destination)||destination>=0)return false;
        var list=await db.SalesLists.AsNoTracking().SingleOrDefaultAsync(l=>l.Id==listId&&!l.IsDeleted&&l.IsInventoryOffer&&
            l.InventoryCode!=null&&l.Status==SalesListStatus.Invoiced&&l.TelegramMessageId!=null&&l.TelegramChannelId!=null,ct);
        if(list is null)return false;
        var chat=list.TelegramChannelId!;var message=list.TelegramMessageId!.Value;
        var url=chat.StartsWith("-100",StringComparison.Ordinal)?$"https://t.me/c/{chat[4..]}/{message}":
            chat.StartsWith('@')?$"https://t.me/{chat[1..]}/{message}":"";
        if(url.Length==0)return false;
        var binding=Binding(list.Id,chat,message);var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("inventory-cleanup-alert:"+binding))[..16]);
        await using var own=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(ct):null;
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"inventory-cleanup-alert:"+id},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Inventory alert busy',1;",ct);
        var existing=await db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==id,ct);
        if(existing!=null)
        {
            if(withdrawn&&!existing.IsDeleted&&existing.Status==NotificationOutboxStatus.Pending)
            {
                var updated=System.Text.Json.Nodes.JsonNode.Parse(existing.Payload)!.AsObject();updated["Withdrawn"]=true;
                existing.Payload=updated.ToJsonString();await db.SaveChangesAsync(ct);
            }
            if(own!=null)await own.CommitAsync(ct);return false;
        }
        var invoices=await db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&i.Status!=ZibasheERP.Domain.Enums.InvoiceStatus.Cancelled&&
            i.Status!=ZibasheERP.Domain.Enums.InvoiceStatus.Draft&&i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&
            db.OrderItems.Any(x=>!x.IsDeleted&&x.OrderId==i.OrderId&&x.SalesListId==list.Id)).Select(i=>i.InvoiceNumber).Distinct().Take(10).ToArrayAsync(ct);
        db.NotificationOutbox.Add(new(){Id=id,Channel="Telegram",EventType=ManualAlertEvent,Recipient=destination.ToString(),CreatedAt=DateTime.UtcNow,
            Payload=JsonSerializer.Serialize(new{Binding=binding,SalesListId=list.Id,InventoryCode=list.DisplayCode,Name=string.IsNullOrWhiteSpace(list.PersianName)?list.EnglishName:list.PersianName,
                PostUrl=url,MessageId=message,InvoiceNumbers=string.Join("، ",invoices),Withdrawn=withdrawn,Reason=restricted?"restricted":"repeated_failure"})});
        await db.SaveChangesAsync(ct);if(own!=null)await own.CommitAsync(ct);return true;
    }

    // Covers retirement receipts saved before alerts were enabled, as well as a
    // process stop between withdrawing an offer and queuing its operator notice.
    public static async Task<int> RecoverManualAlertsAsync(AppDbContext db,string failureChat,CancellationToken ct=default)
    {
        if(!long.TryParse(failureChat.Trim(),out var target)||target>=0)return 0;
        var ids=await db.SalesLists.AsNoTracking().Where(l=>!l.IsDeleted&&l.IsInventoryOffer&&l.Status==SalesListStatus.Invoiced&&
            l.TelegramMessageId!=null&&l.TelegramChannelId!=null&&
            db.NotificationOutbox.Any(r=>!r.IsDeleted&&r.Channel==RetirementChannel&&r.Status==NotificationOutboxStatus.Processed&&
                r.Recipient==l.Id.ToString()+":"+l.TelegramChannelId+":"+l.TelegramMessageId.Value.ToString())&&
            !db.NotificationOutbox.Any(n=>n.Channel=="Telegram"&&n.EventType==ManualAlertEvent&&
                n.Payload.Contains("\"Binding\":\""+l.Id.ToString()+":"+l.TelegramChannelId+":"+l.TelegramMessageId.Value.ToString()+"\"")))
            .OrderBy(l=>l.Id).Select(l=>l.Id).Take(10).ToArrayAsync(ct);
        var queued=0;foreach(var id in ids)if(await QueueManualAlertAsync(db,id,failureChat,true,true,ct))queued++;
        return queued;
    }

    public static NotificationOutbox Job(Guid listId) => new()
    {
        Id=Guid.NewGuid(), CreatedAt=DateTime.UtcNow, Channel="ReadyInventoryRefresh",
        EventType="InventoryInvoiced", Recipient=listId.ToString(), Payload="{}"
    };

    // Recover older invoices and publication receipts arriving after invoicing.
    public static async Task<int> RecoverAsync(AppDbContext db,CancellationToken ct=default)
    {
        var ids=await db.SalesLists.AsNoTracking().Where(list=>!list.IsDeleted&&list.IsInventoryOffer&&
            list.InventoryCode!=null&&list.Status==SalesListStatus.Invoiced&&list.TelegramMessageId!=null&&
            list.TelegramChannelId!=null&&!db.NotificationOutbox.Any(receipt=>!receipt.IsDeleted&&receipt.Channel==RetirementChannel&&
                receipt.Status==NotificationOutboxStatus.Processed&&receipt.Recipient==list.Id.ToString()+":"+list.TelegramChannelId+":"+list.TelegramMessageId.Value.ToString())&&
            !db.NotificationOutbox.Any(job=>!job.IsDeleted&&
                job.Channel=="ReadyInventoryRefresh"&&job.Recipient==list.Id.ToString()&&
                (job.Status==NotificationOutboxStatus.Pending||job.Status==NotificationOutboxStatus.Processing)))
            .OrderBy(list=>list.Id).Select(list=>list.Id).Take(25).ToArrayAsync(ct);
        foreach(var id in ids)db.NotificationOutbox.Add(Job(id));
        if(ids.Length>0)await db.SaveChangesAsync(ct);
        return ids.Length;
    }
}

public sealed class InventoryPostRetryException(int seconds):InvalidOperationException("TelegramInventoryPostDeleteRetryRequired")
{
    public int RetryAfterSeconds {get;}=Math.Max(1,seconds);
}
