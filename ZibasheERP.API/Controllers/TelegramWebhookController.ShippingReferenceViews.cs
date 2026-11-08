using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private const string ShippingReferenceViewChannel="ShippingReferenceView";
    private Guid? _shippingReferenceViewId;
    private sealed record ShippingReferenceView(Guid CustomerId,string ChatId,long[] MessageIds,long[] ControlMessageIds,long[] DeletedIds,string State="open");
    private async Task<Guid> OpenShippingReferenceViewAsync(ShippingReferenceContext context,CancellationToken ct)
    {
        if(context.ViewId.HasValue)
        {
            var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>!n.IsDeleted&&n.Id==context.ViewId&&n.Channel==ShippingReferenceViewChannel,ct);
            var view=row is null?null:JsonSerializer.Deserialize<ShippingReferenceView>(row.Payload);
            if(view is null||view.CustomerId!=context.CustomerId||view.ChatId!=context.ChatId||view.State!="open")
                throw new InvalidOperationException("این گزارش بسته شده است؛ برای مشاهدهٔ دوباره از دکمهٔ زیر آدرس استفاده کنید.");
            return row!.Id;
        }
        var id=Guid.NewGuid();var data=new ShippingReferenceView(context.CustomerId,context.ChatId,[],[],[]);
        _db.NotificationOutbox.Add(new(){Id=id,CustomerId=context.CustomerId,Channel=ShippingReferenceViewChannel,EventType="ClosableReport",Recipient=context.ChatId,
            Payload=JsonSerializer.Serialize(data),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);return id;
    }
    private async Task<TelegramSendResult> SendShippingReferenceResultAsync(Func<Task<TelegramSendResult>> send,CancellationToken ct,bool control=false)
    {
        if(!_shippingReferenceViewId.HasValue)return await send();
        var id=_shippingReferenceViewId.Value;
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id&&n.Channel==ShippingReferenceViewChannel,ct);
        if(JsonSerializer.Deserialize<ShippingReferenceView>(row.Payload)!.State!="open")return new(false,"گزارش بسته شده است.");
        var sent=await send();if(!sent.IsSuccessful||!sent.MessageId.HasValue)return sent;
        var saved=await MutateShippingPayloadAsync<ShippingReferenceView>(id,ShippingReferenceViewChannel,d=>d with
        {MessageIds=d.MessageIds.Append(sent.MessageId.Value).Distinct().ToArray(),ControlMessageIds=control?d.ControlMessageIds.Append(sent.MessageId.Value).Distinct().ToArray():d.ControlMessageIds},ct);
        // A result delivered while Close was running belongs to this same view,
        // and must be cleaned up too; no neighboring message IDs are ever guessed.
        if(saved.State!="open")await DeleteShippingReferenceViewMessagesAsync(id,saved,ct);
        return sent;
    }
    private Task<TelegramSendResult> ShippingReferenceTextAsync(long chat,string text,CancellationToken ct)=>
        SendShippingReferenceResultAsync(()=>_sender.SendAsync(chat.ToString(),text,ct),ct);
    private Task<TelegramSendResult> ShippingReferencePhotoAsync(string chat,string file,string text,CancellationToken ct)=>
        SendShippingReferenceResultAsync(()=>_sender.SendPhotoAsync(chat,file,text,ct),ct);
    private async Task FinishShippingReferenceViewAsync(long chat,CancellationToken ct)
    {
        if(!_shippingReferenceViewId.HasValue)return;
        var id=_shippingReferenceViewId.Value;
        await SendShippingReferenceResultAsync(()=>_sender.SendInlineKeyboardAsync(chat.ToString(),"برای خلوت ماندن گروه، پس از بررسی نتیجه آن را ببندید.",
            [[new("✖️ بستن و حذف نتیجه",ShippingReferencePrefix+"close:"+id.ToString("N"))]],ct),ct,true);
    }
    private async Task<bool> DeleteShippingReferenceViewMessagesAsync(Guid id,ShippingReferenceView view,CancellationToken ct)
    {
        var failed=false;
        foreach(var messageId in view.MessageIds.Except(view.DeletedIds).OrderBy(m=>view.ControlMessageIds.Contains(m)?1:0))
        {
            if(failed&&view.ControlMessageIds.Contains(messageId))continue; // Keep a working retry button.
            var result=await _sender.DeleteMessageAsync(view.ChatId,messageId,ct);
            if(!result.IsSuccessful&&result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true){failed=true;continue;}
            await MutateShippingPayloadAsync<ShippingReferenceView>(id,ShippingReferenceViewChannel,d=>d with{DeletedIds=d.DeletedIds.Append(messageId).Distinct().ToArray()},ct);
        }
        await MutateShippingPayloadAsync<ShippingReferenceView>(id,ShippingReferenceViewChannel,d=>d with{State=d.MessageIds.Except(d.DeletedIds).Any()?"closing":"closed"},ct);
        return !failed;
    }
    private async Task CloseShippingReferenceViewAsync(TelegramCallbackQuery callback,Guid id,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>!n.IsDeleted&&n.Id==id&&n.Channel==ShippingReferenceViewChannel,ct);
        var view=row is null?null:JsonSerializer.Deserialize<ShippingReferenceView>(row.Payload);
        if(view is null||view.ChatId!=callback.Message!.Chat.Id.ToString()||row!.CustomerId!=view.CustomerId||!view.ControlMessageIds.Contains(callback.Message.MessageId))
        {await _sender.AnswerCallbackAsync(callback.Id,"این دکمه متعلق به همین گزارش نیست.",ct,true);return;}
        view=await MutateShippingPayloadAsync<ShippingReferenceView>(id,ShippingReferenceViewChannel,d=>d with{State="closing"},ct);
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        if(!await DeleteShippingReferenceViewMessagesAsync(id,view,ct))
            await _sender.AnswerCallbackAsync(callback.Id,"حذف بعضی پیام‌ها ممکن نشد؛ دوباره «بستن» را بزنید. دسترسی حذف پیام ربات هم بررسی شود.",ct,true);
    }
}
