using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal const string ShippingDecantStreamChannel="ShippingDecantReferenceSend";
    private sealed record ShippingDecantPhoto(string File,string Caption);
    private sealed record ShippingDecantStream(ShippingReferenceContext Reference,long Actor,string Fingerprint,
        ShippingDecantPhoto[] Photos,string[] Missing,int Next=0,int? Attempted=null,int Failed=0,bool SummaryAttempted=false);
    private static string ShippingDecantFingerprint(IEnumerable<OrderItem> items)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(items.OrderBy(i=>i.Id).Select(i=>new{i.Id,i.SalesListId,i.RequestedVolumeMl})))));
    private static ShippingDecantPhoto[] BuildShippingDecantPhotos(IEnumerable<(string File,string Name,decimal Volume)> rows)
    {
        var photos=new List<ShippingDecantPhoto>();
        foreach(var group in rows.GroupBy(p=>p.File))
        {
            const string prefix="📸 عکس دکانت\n";var caption=prefix;
            foreach(var product in group.GroupBy(p=>p.Name))
            {
                var name=product.Key;if(name.Length>700)name=name[..(char.IsHighSurrogate(name[699])?699:700)]+"…";
                var entry=$"{name}\nمقدار این مشتری: {product.Sum(p=>p.Volume):0.#} میل";
                if(caption.Length+entry.Length+2>950&&caption!=prefix){photos.Add(new(group.Key,caption));caption=prefix;}
                caption+=(caption==prefix?"":"\n\n")+entry;
            }
            if(caption!=prefix)photos.Add(new(group.Key,caption));
        }
        return photos.ToArray();
    }
    private async Task QueueShippingDecantStreamAsync(ShippingReferenceContext reference,OrderItem[] items,long actor,
        ShippingDecantPhoto[] photos,string[] missing,CancellationToken ct)
    {
        if(reference.ViewId is null)throw new InvalidOperationException("مرجع گزارش عکس مشخص نیست.");
        reference=reference with{ItemIds=items.Select(i=>i.Id).ToArray()};
        var data=new ShippingDecantStream(reference,actor,ShippingDecantFingerprint(items),photos,missing);
        _db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=reference.CustomerId,Channel=ShippingDecantStreamChannel,
            EventType="ShowAllParcelDecantPhotos",Recipient=reference.ChatId,Payload=JsonSerializer.Serialize(data),CreatedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);
        await ShippingReferenceTextAsync(long.Parse(reference.ChatId),photos.Length==0?"عکس دکانت قابل تطبیق پیدا نشد؛ نتیجهٔ بررسی نمایش داده می‌شود.":
            $"📸 نمایش خودکار {photos.Length} عکس دکانت شروع شد؛ همهٔ عکس‌ها پشت‌سرهم نمایش داده می‌شوند.",ct);
    }
    internal async Task<int?> ProcessShippingDecantStreamAsync(NotificationOutbox job,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==job.Id&&n.Channel==ShippingDecantStreamChannel&&!n.IsDeleted,ct);
        var data=JsonSerializer.Deserialize<ShippingDecantStream>(row.Payload)!;var context=data.Reference;
        if(context.ViewId is null||job.CustomerId!=context.CustomerId||job.Recipient!=context.ChatId||context.ChatId!=_options.ShippingChatId.Trim())
            throw new InvalidOperationException("Decant stream source mismatch");
        var view=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==context.ViewId&&n.Channel==ShippingReferenceViewChannel&&!n.IsDeleted,ct);
        var state=view is null?null:JsonSerializer.Deserialize<ShippingReferenceView>(view.Payload);
        if(state is null||state.State!="open")return null;
        if(state.CustomerId!=context.CustomerId||state.ChatId!=context.ChatId)throw new InvalidOperationException("Decant stream view mismatch");
        _shippingReferenceViewId=context.ViewId;
        try
        {
            if(!await IsAuthorizedShippingAdminAsync(long.Parse(context.ChatId),data.Actor,ct))return null;
            if(data.Next<data.Photos.Length)
            {
                OrderItem[] current;
                try{current=await LoadShippingReferenceItemsAsync(context,ct);}
                catch(InvalidOperationException)
                {await ShippingReferenceTextAsync(long.Parse(context.ChatId),"⚠️ اقلام آمادهٔ بسته قابل تأیید نیست؛ گزارش عکس را پس از بررسی بسته دوباره باز کنید.",ct);return null;}
                if(ShippingDecantFingerprint(current.Where(i=>context.ItemIds.Contains(i.Id)))!=data.Fingerprint)
                {
                    await ShippingReferenceTextAsync(long.Parse(context.ChatId),"⚠️ اقلام آمادهٔ بسته تغییر کرده؛ برای نمایش عکس‌های به‌روز دوباره از دکمهٔ زیر آدرس استفاده کنید.",ct);return null;
                }
                var index=data.Next;
                if(data.Attempted.HasValue)
                {
                    await MutateShippingPayloadAsync<ShippingDecantStream>(job.Id,ShippingDecantStreamChannel,d=>d with{Next=index+1,Attempted=null,Failed=d.Failed+1},ct);
                    return 3; // An interrupted unknown HTTP result is not sent a second time.
                }
                await MutateShippingPayloadAsync<ShippingDecantStream>(job.Id,ShippingDecantStreamChannel,d=>d with{Attempted=index},ct);
                var photo=data.Photos[index];
                var sent=await SendShippingReferenceResultAsync(()=>_sender.SendPhotoOnceWithKeyboardAsync(context.ChatId,photo.File,photo.Caption,[],ct),ct);
                if(sent.IsRetryable)
                {
                    await MutateShippingPayloadAsync<ShippingDecantStream>(job.Id,ShippingDecantStreamChannel,d=>d with{Attempted=null},ct);
                    return Math.Max(3,sent.RetryAfterSeconds??60);
                }
                await MutateShippingPayloadAsync<ShippingDecantStream>(job.Id,ShippingDecantStreamChannel,d=>d with
                    {Next=index+1,Attempted=null,Failed=d.Failed+(sent.IsSuccessful&&sent.MessageId is >0?0:1)},ct);
                return 3;
            }
            if(data.SummaryAttempted)return null;
            await MutateShippingPayloadAsync<ShippingDecantStream>(job.Id,ShippingDecantStreamChannel,d=>d with{SummaryAttempted=true},ct);
            var text=$"📸 نمایش عکس‌های دکانت پایان یافت: {data.Photos.Length-data.Failed} از {data.Photos.Length}"+
                (data.Failed>0?$"\nتحویل تأییدنشده یا ناموفق: {data.Failed}":"");
            if(data.Missing.Length>0)text+="\nعکس دکانت قابل تطبیق ثبت نشده:\n"+string.Join("\n",data.Missing.Take(12).Select(n=>"• "+n))+
                (data.Missing.Length>12?$"\nو {data.Missing.Length-12} مورد دیگر":"");
            await SendShippingReferenceResultAsync(()=>_sender.SendInlineKeyboardAsync(context.ChatId,text,
                [[new("✖️ بستن و حذف نتیجه",ShippingReferencePrefix+"close:"+context.ViewId.Value.ToString("N"))]],ct),ct,true);return null;
        }
        finally{_shippingReferenceViewId=null;}
    }
}
