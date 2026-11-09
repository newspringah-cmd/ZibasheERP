using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;

public static class ShippingTrackingNotices
{
    public sealed class AddressMessageMissingException:InvalidOperationException {}
    public sealed class ButtonRetryException(int seconds):InvalidOperationException {public int RetryAfterSeconds{get;}=seconds;}
    public const string Channel="ShippingTrackingAddressNotice";
    public const string Text="✅ کد رهگیری ارسال شد";
    public sealed record Data(Guid CustomerId,string ChatId,long SourceMessageId,Guid? RequestId,
        DateTime? AttemptedAt=null,DateTime? SentAt=null,long? MessageId=null,bool ButtonUpdated=false);
    public sealed record Source(Guid CustomerId,Guid? RequestId,Guid[] ItemIds,string ChatId,string Kind,long? MessageId,bool Installed);

    public static async Task<bool> SourceMatchesAsync(AppDbContext db,Data data,CancellationToken ct)
    {
        var roots=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="ShippingReferenceContext"&&n.EventType=="AddressReferences"&&
            n.CustomerId==data.CustomerId&&n.Recipient==data.ChatId).Select(n=>n.Payload).ToArrayAsync(ct);
        foreach(var payload in roots)
        {
            Source? root;try{root=JsonSerializer.Deserialize<Source>(payload);}catch(JsonException){continue;}
            if(root is {Kind:"root",Installed:true}&&root.CustomerId==data.CustomerId&&root.ChatId==data.ChatId&&
                root.MessageId==data.SourceMessageId&&(!data.RequestId.HasValue||root.RequestId==data.RequestId))return true;
        }
        if(data.RequestId is not Guid request)return false;
        if(!await db.OrderItems.AsNoTracking().AnyAsync(i=>!i.IsDeleted&&i.ShippingRequestId==request&&i.ShippingTelegramMessageId==data.SourceMessageId,ct))return false;
        try {return (await ShippingPackageService.ValidatePackageAsync(db,request,data.CustomerId,ct)).Id==data.CustomerId;}
        catch(InvalidOperationException){return false;}
    }

    public static async Task<bool> QueueAsync(AppDbContext db,TelegramOptions options,Data data,CancellationToken ct)
    {
        if(data.SourceMessageId<=0||string.IsNullOrWhiteSpace(options.ShippingChatId)||data.ChatId!=options.ShippingChatId.Trim()||
            !await SourceMatchesAsync(db,data,ct))return false;
        var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"tracking-address-notice:{data.ChatId}:{data.SourceMessageId}"))[..16]);
        var existing=await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct);
        if(existing!=null)
        {
            if(existing.Channel!=Channel||existing.CustomerId!=data.CustomerId||existing.Recipient!=data.ChatId)
                throw new InvalidOperationException("مرجع اعلان رهگیری با آدرس یکسان نیست.");
            return true;
        }
        var payload=JsonSerializer.Serialize(data);var now=DateTime.UtcNow;
        // Do not flush a caller's pending Sent state before parcel completion.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox (Id,CustomerId,Channel,EventType,Recipient,Payload,Status,Attempts,CreatedAt,IsDeleted)
            SELECT {id},{data.CustomerId},{Channel},N'TrackingDeliveredAddressNotice',{data.ChatId},{payload},1,0,{now},CAST(0 AS bit)
            WHERE NOT EXISTS(SELECT 1 FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id});
            """,ct);
        return true;
    }

    public static async Task DeliverAsync(AppDbContext db,ITelegramMessageSender sender,TelegramOptions options,NotificationOutbox job,CancellationToken ct,Func<Data,CancellationToken,Task>? updateButton=null)
    {
        var data=JsonSerializer.Deserialize<Data>(job.Payload)??throw new InvalidOperationException("Missing notice data");
        if(job.Channel!=Channel||job.CustomerId!=data.CustomerId||job.Recipient!=data.ChatId||data.ChatId!=options.ShippingChatId.Trim()||
            !await SourceMatchesAsync(db,data,ct))
        {job.Status=NotificationOutboxStatus.Failed;job.LastError="Address notice scope is no longer valid";return;}
        // Idempotent markup edits retry independently of the one-shot notice reply.
        if(!data.ButtonUpdated&&updateButton is not null)
        {
            try{await updateButton(data,ct);}
            catch(AddressMessageMissingException){job.Status=NotificationOutboxStatus.Failed;job.LastError="Tracking address message no longer exists";return;}
            data=data with{ButtonUpdated=true};job.Payload=JsonSerializer.Serialize(data);await db.SaveChangesAsync(ct);
        }
        if(data.SentAt.HasValue){job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=data.SentAt;return;}
        if(data.AttemptedAt.HasValue)
        {job.Status=NotificationOutboxStatus.Failed;job.LastError="Address notice outcome unknown; automatic duplicate reply refused";return;}
        data=data with{AttemptedAt=DateTime.UtcNow};job.Payload=JsonSerializer.Serialize(data);
        await db.SaveChangesAsync(ct);
        var result=await sender.SendReplyOnceAsync(data.ChatId,Text,data.SourceMessageId,ct);
        if(result.IsSuccessful&&result.MessageId is >0)
        {
            data=data with{SentAt=DateTime.UtcNow,MessageId=result.MessageId};job.Payload=JsonSerializer.Serialize(data);
            job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=data.SentAt;job.NextAttemptAt=null;job.LastError=null;
        }
        else if(result.IsRetryable)
        {
            job.Payload=JsonSerializer.Serialize(data with{AttemptedAt=null});job.Status=NotificationOutboxStatus.Pending;
            job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Max(1,result.RetryAfterSeconds??60)+1d);job.LastError="Telegram cooldown";
        }
        else {job.Status=NotificationOutboxStatus.Failed;job.LastError="Address reply was not acknowledged; no automatic duplicate";}
    }
}
