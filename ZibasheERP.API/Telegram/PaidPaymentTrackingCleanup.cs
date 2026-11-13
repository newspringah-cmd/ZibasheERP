using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class PaidPaymentTrackingCleanup(AppDbContext db, ITelegramMessageSender sender)
{
    public const string Channel="PaidPaymentTrackingCleanup";
    public sealed record Target(Guid BatchId,Guid SalesListId,string ChatId,long? MessageId,long? PhotoId);
    public sealed record Receipt(Target Target,DateTime? TextDeletedAtUtc=null,DateTime? PhotoDeletedAtUtc=null);
    public static Guid Key(Target target)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"paid-payment-tracking:{target.BatchId:N}:{target.SalesListId:N}:{target.MessageId}:{target.PhotoId}"))[..16]);

    private IQueryable<InvoiceIssuanceBatchSalesList> Eligible(string chat)=>db.InvoiceIssuanceBatchSalesLists.Where(link=>
        link.TelegramPaymentTrackingChatId==chat&&
        (link.TelegramPaymentTrackingMessageId!=null||link.TelegramPaymentTrackingPhotoMessageId!=null)&&
        !link.InvoiceIssuanceBatch.IsDeleted&&link.InvoiceIssuanceBatch.Status==InvoiceIssuanceBatchStatus.Issued&&
        db.Orders.Any(o=>o.InvoiceIssuanceBatchId==link.InvoiceIssuanceBatchId&&o.Items.Any(i=>i.SalesListId==link.SalesListId))&&
        !db.Orders.Any(o=>o.InvoiceIssuanceBatchId==link.InvoiceIssuanceBatchId&&o.Items.Any(i=>i.SalesListId==link.SalesListId)&&
            (o.IsDeleted||o.Invoices.Count(i=>!i.IsDeleted)!=1||o.Invoices.Any(i=>!i.IsDeleted&&i.Status!=InvoiceStatus.Paid))));

    public Task<Target[]> CandidatesAsync(string chat,CancellationToken ct)=>Eligible(chat).AsNoTracking()
        .OrderBy(l=>l.InvoiceIssuanceBatch.IssuedAt).Select(l=>new Target(l.InvoiceIssuanceBatchId,l.SalesListId,
            l.TelegramPaymentTrackingChatId!,l.TelegramPaymentTrackingMessageId,l.TelegramPaymentTrackingPhotoMessageId)).ToArrayAsync(ct);

    public async Task<bool> ProcessAsync(Target target,string configuredChat,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(configuredChat)||target.ChatId!=configuredChat)
            throw new InvalidOperationException("Payment cleanup destination mismatch");
        // Hold the eligibility reads until deletion finishes: a payment reversal cannot
        // race between the final paid check and the Telegram request.
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var resource=$"paid-payment-tracking:{target.BatchId:N}:{target.SalesListId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={resource},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Payment report cleanup busy',1;",ct);
        var link=await Eligible(configuredChat).SingleOrDefaultAsync(l=>l.InvoiceIssuanceBatchId==target.BatchId&&l.SalesListId==target.SalesListId,ct);
        if(link is null||link.TelegramPaymentTrackingMessageId!=target.MessageId||link.TelegramPaymentTrackingPhotoMessageId!=target.PhotoId)
        {await tx.CommitAsync(ct);return false;}
        var id=Key(target);var job=await db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==id,ct);
        if(job?.NextAttemptAt>DateTime.UtcNow){await tx.CommitAsync(ct);return false;}
        var receipt=job is null?new Receipt(target):JsonSerializer.Deserialize<Receipt>(job.Payload)??throw new InvalidOperationException("Invalid payment cleanup receipt");
        if(receipt.Target!=target)throw new InvalidOperationException("Payment report target changed");
        if(job is null){job=new(){Id=id,CustomerId=Guid.Empty,Channel=Channel,EventType="DeleteFullyPaidListReport",Recipient=configuredChat,CreatedAt=DateTime.UtcNow};db.NotificationOutbox.Add(job);}
        async Task<bool> Delete(long? message)
        {
            if(message is not >0)return true;
            var result=await sender.DeleteMessageAsync(configuredChat,message.Value,ct);
            if(result.IsSuccessful||result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)==true)return true;
            job.Attempts++;job.LastError=result.Error;
            var permanent=result.Error?.Contains("can't be deleted",StringComparison.OrdinalIgnoreCase)==true||
                result.Error?.Contains("not enough rights",StringComparison.OrdinalIgnoreCase)==true||
                result.Error?.Contains("not enough privileges",StringComparison.OrdinalIgnoreCase)==true;
            job.Status=permanent?NotificationOutboxStatus.Failed:NotificationOutboxStatus.Pending;
            job.NextAttemptAt=DateTime.UtcNow.AddSeconds(permanent?86400:Math.Max(30,result.RetryAfterSeconds??60));
            return false;
        }
        if(receipt.PhotoDeletedAtUtc is null&&await Delete(target.PhotoId))receipt=receipt with{PhotoDeletedAtUtc=DateTime.UtcNow};
        if(receipt.PhotoDeletedAtUtc.HasValue&&receipt.TextDeletedAtUtc is null&&await Delete(target.MessageId))receipt=receipt with{TextDeletedAtUtc=DateTime.UtcNow};
        var complete=receipt.PhotoDeletedAtUtc.HasValue&&receipt.TextDeletedAtUtc.HasValue;
        job.Payload=JsonSerializer.Serialize(receipt);job.UpdatedAt=DateTime.UtcNow;
        if(complete)
        {
            job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.NextAttemptAt=null;job.LastError=null;
            link.TelegramPaymentTrackingMessageId=null;link.TelegramPaymentTrackingPhotoMessageId=null;
            // The receipt retains original message IDs for audit; invoice/payment data is untouched.
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return complete;
    }
}
