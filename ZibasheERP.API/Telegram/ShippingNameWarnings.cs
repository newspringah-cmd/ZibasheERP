using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;
public static class ShippingNameWarnings
{
    public const string Channel="ShippingNameWarning";
    public const string Heading="❌❌❌ دقت شود؛ تشابه اسمی وجود دارد";
    public sealed record Recipient(Guid ReferenceId,Guid CustomerId,long MessageId,Guid? RequestId,string Name,string Username);
    public sealed record Data(Recipient Source,Recipient[] Peers,string Fingerprint,bool Active=true,long? MessageId=null,DateTime? AttemptedAt=null);
    private static string Serialize(Data data)=>JsonSerializer.Serialize(data);
    private static Guid Key(string chat,long message)=>new(SHA256.HashData(Encoding.UTF8.GetBytes($"shipping-name-warning:{chat}:{message}"))[..16]);
    public static async Task<Recipient[]> ReadyAsync(AppDbContext db,TelegramOptions options,CancellationToken ct)
    {
        var roots=await TrackingPreparedAddresses.LoadForNameWarningsAsync(db,options.ShippingChatId.Trim(),ct);
        var ids=roots.Select(r=>r.AddressId).Distinct().ToArray();
        var addresses=await db.Addresses.AsNoTracking().Where(a=>ids.Contains(a.Id)&&!a.IsDeleted).ToDictionaryAsync(a=>a.Id,ct);
        return roots.Where(r=>addresses.ContainsKey(r.AddressId)).Select(r=>new{Root=r,Name=ShippingAddressDisplay.RecipientName(addresses[r.AddressId])})
            .Where(r=>r.Name is not null).Select(r=>new Recipient(r.Root.ReferenceId,r.Root.CustomerId,r.Root.MessageId,r.Root.RequestId,r.Name!,r.Root.Username.TrimStart('@'))).ToArray();
    }
    public static async Task ReconcileAsync(AppDbContext db,TelegramOptions options,CancellationToken ct)
    {
        var chat=options.ShippingChatId.Trim();if(!chat.StartsWith('-'))return;
        var active=await ReadyAsync(db,options,ct);
        var rows=await db.NotificationOutbox.Where(n=>!n.IsDeleted&&n.Channel==Channel&&n.Recipient==chat&&
            (n.Payload.Contains("\"Active\":true")||n.Status==NotificationOutboxStatus.Pending||n.Status==NotificationOutboxStatus.Processing)).ToArrayAsync(ct);
        var byId=rows.ToDictionary(r=>r.Id);var desired=new HashSet<Guid>();
        foreach(var source in active)
        {
            var peers=active.Where(p=>p.MessageId!=source.MessageId&&ShippingRecipientNameSimilarity.Similar(source.Name,p.Name)).OrderBy(p=>p.MessageId).ToArray();
            if(peers.Length==0)continue;
            var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Source=source,Peers=peers}))));
            var id=Key(chat,source.MessageId);desired.Add(id);
            if(!byId.TryGetValue(id,out var row))
            {
                // A previously cleared record can be reactivated without creating duplicate intents.
                row=await db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==id,ct);
                if(row is null){db.NotificationOutbox.Add(new(){Id=id,CustomerId=source.CustomerId,Channel=Channel,EventType="RecipientNameCollision",Recipient=chat,
                    Payload=Serialize(new(source,peers,fingerprint)),CreatedAt=DateTime.UtcNow});continue;}
            }
            var prior=JsonSerializer.Deserialize<Data>(row.Payload)!;
            if(row.CustomerId!=source.CustomerId||prior.Source.MessageId!=source.MessageId||row.Channel!=Channel||row.Recipient!=chat)throw new InvalidOperationException("Name warning source changed identity");
            if(prior.Active&&prior.Fingerprint==fingerprint)continue;
            var updated=prior with{Source=source,Peers=peers,Fingerprint=fingerprint,Active=true};row.Payload=Serialize(updated);
            row.Status=updated.MessageId is null&&updated.AttemptedAt is not null?NotificationOutboxStatus.Failed:NotificationOutboxStatus.Pending;
            row.NextAttemptAt=DateTime.UtcNow;row.UpdatedAt=DateTime.UtcNow;
        }
        foreach(var row in rows.Where(r=>!desired.Contains(r.Id)))
        {
            var prior=JsonSerializer.Deserialize<Data>(row.Payload)!;if(!prior.Active)continue;
            row.Payload=Serialize(prior with{Active=false});row.Status=NotificationOutboxStatus.Pending;row.NextAttemptAt=DateTime.UtcNow;row.UpdatedAt=DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
    private static string Label(Recipient r)=>r.Name+(r.Username.Length>0?" — @"+r.Username:string.Empty);
    private static string Link(string chat,long message)=>chat.StartsWith("-100",StringComparison.Ordinal)?"https://t.me/c/"+chat[4..]+"/"+message:"پیام آدرس: "+message;
    public static string Text(string chat,Data data)
    {
        var text=Heading+"\nگیرندهٔ این بسته: "+Label(data.Source)+"\n\nبسته‌های آمادهٔ ارسال با نام مشابه:\n";
        var shown=0;
        foreach(var peer in data.Peers)
        {
            var line="• "+Label(peer)+"\n"+Link(chat,peer.MessageId)+"\n";
            if(text.Length+line.Length>3600)break;text+=line;shown++;
        }
        if(shown<data.Peers.Length)text+=$"و {data.Peers.Length-shown} آدرس آمادهٔ دیگر با نام مشابه.\n";
        return text+"\nلطفاً آیدی و آدرس گیرنده را پیش از آماده‌سازی و ارسال با دقت بررسی کنید.";
    }
    public static async Task ProcessAsync(AppDbContext db,ITelegramMessageSender sender,TelegramOptions options,NotificationOutbox job,CancellationToken ct)
    {
        var data=JsonSerializer.Deserialize<Data>(job.Payload)??throw new InvalidOperationException("Missing name warning");
        if(job.Channel!=Channel||job.Recipient!=options.ShippingChatId.Trim()||job.CustomerId!=data.Source.CustomerId||job.Id!=Key(job.Recipient,data.Source.MessageId))
        {job.Status=NotificationOutboxStatus.Failed;job.LastError="Name warning scope mismatch";return;}
        void Done(){job.Status=NotificationOutboxStatus.Processed;job.ProcessedAt=DateTime.UtcNow;job.NextAttemptAt=null;job.LastError=null;}
        void Retry(TelegramSendResult result){job.Status=NotificationOutboxStatus.Pending;job.Attempts++;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Max(30,result.RetryAfterSeconds??30)+1);job.LastError="Name warning Telegram retry";}
        if(!data.Active)
        {
            if(data.MessageId is long message)
            {
                if(message==data.Source.MessageId||data.Peers.Any(p=>p.MessageId==message))throw new InvalidOperationException("A warning cannot delete an address");
                var removed=await sender.DeleteMessageAsync(job.Recipient,message,ct);
                if(!removed.IsSuccessful&&removed.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
                {
                    if(removed.IsRetryable){Retry(removed);return;}
                    var cleared=await sender.EditTextWithKeyboardAsync(job.Recipient,message,"✅ هشدار تشابه اسمی برطرف شد؛ بستهٔ مشابه دیگری در انتظار ارسال نیست.",[],ct);
                    if(!cleared.IsSuccessful&&cleared.Error?.Contains("message is not modified",StringComparison.OrdinalIgnoreCase)!=true){Retry(cleared);return;}
                }
            }
            else if(data.AttemptedAt.HasValue){job.Status=NotificationOutboxStatus.Failed;job.LastError="Unknown warning delivery cannot be blindly deleted or duplicated";return;}
            job.Payload=Serialize(data with{MessageId=null,AttemptedAt=null});Done();return;
        }
        if(!await ShippingTrackingNotices.SourceMatchesAsync(db,new(data.Source.CustomerId,job.Recipient,data.Source.MessageId,data.Source.RequestId),ct))
        {job.Status=NotificationOutboxStatus.Failed;job.LastError="Warning source no longer matches its address";return;}
        var text=Text(job.Recipient,data);
        if(data.MessageId is long priorMessage)
        {
            if(priorMessage==data.Source.MessageId||data.Peers.Any(p=>p.MessageId==priorMessage))throw new InvalidOperationException("A warning cannot overwrite an address");
            var edited=await sender.EditTextWithKeyboardAsync(job.Recipient,priorMessage,text,[],ct);
            if(edited.IsSuccessful||edited.Error?.Contains("message is not modified",StringComparison.OrdinalIgnoreCase)==true){Done();return;}
            if(edited.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)==true)job.Payload=Serialize(data with{MessageId=null,AttemptedAt=null});
            Retry(edited);return;
        }
        if(data.AttemptedAt.HasValue){job.Status=NotificationOutboxStatus.Failed;job.LastError="Warning delivery outcome unknown; automatic duplicate refused";return;}
        job.Payload=Serialize(data with{AttemptedAt=DateTime.UtcNow});await db.SaveChangesAsync(ct);
        var result=await sender.SendReplyOnceAsync(job.Recipient,text,data.Source.MessageId,ct);
        if(result.IsSuccessful&&result.MessageId is >0&&result.MessageId!=data.Source.MessageId&&!data.Peers.Any(p=>p.MessageId==result.MessageId))
        {job.Payload=Serialize(data with{AttemptedAt=DateTime.UtcNow,MessageId=result.MessageId});Done();}
        else if(result.IsRetryable){job.Payload=Serialize(data with{AttemptedAt=null});Retry(result);}
        else {job.Status=NotificationOutboxStatus.Failed;job.LastError="Warning reply not acknowledged; no automatic duplicate";}
    }
}
