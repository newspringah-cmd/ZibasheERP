using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    internal async Task<int?> ProcessShippingPackApprovalSendAsync(NotificationOutbox job,CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(job.Payload);var id=payload.RootElement.GetProperty("ApprovalId").GetGuid();
        async Task<ShippingPackApprovalData> Read()
        {
            var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel,ct);
            if(row.CustomerId!=job.CustomerId)throw new InvalidOperationException("گیرندهٔ صف عکس با درخواست یکسان نیست.");
            return JsonSerializer.Deserialize<ShippingPackApprovalData>(row.Payload)!;
        }
        async Task Review(string reason)
        {
            var d=await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>
                d.State is ShippingPackApprovalState.Dispatched or ShippingPackApprovalState.Confirmed?d:d with{State=ShippingPackApprovalState.NeedsReview},ct);
            await _sender.SendAsync(d.ShippingChatId,"⚠️ ارسال عکس بسته نیازمند بررسی است.\n"+reason+"\nارسال خودکار تکراری انجام نمی‌شود.",ct);
        }
        var data=await Read();
        if(data.State is ShippingPackApprovalState.Dispatched or ShippingPackApprovalState.RevisionRequested or ShippingPackApprovalState.Superseded)return null;
        if(!await IsAuthorizedShippingAdminAsync(long.Parse(data.ShippingChatId),data.OperatorUserId,ct)){await Review("دسترسی ثبت‌کننده دیگر فعال نیست.");return null;}
        if(data.PhotoFileIds.Length<1){await Review("تعداد عکس معتبر نیست.");return null;}
        var groups=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==data.CustomerId).Select(g=>g.ChatId).Distinct().ToArrayAsync(ct);
        if(groups.Length==0){await Review("گروه مشتری متصل نیست؛ عکس‌ها محفوظ هستند.");return null;}
        foreach(var chat in groups)
        for(var index=0;index<data.PhotoFileIds.Length;index++)
        {
            data=await Read();if(data.State is ShippingPackApprovalState.Dispatched or ShippingPackApprovalState.RevisionRequested or ShippingPackApprovalState.Superseded)return null;
            if(data.LegacyAddressOnly)
            {
                try{await AssertShippingPackApprovalContentsAsync(data,ct);}
                catch(InvalidOperationException e){await Review(e.Message);return null;}
            }
            var scope=ShippingPackApprovalScope(data);
            var latest=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==data.CustomerId&&n.Recipient==scope)
                .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Id).FirstAsync(ct);
            if(latest!=id)
            {await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>d with{State=ShippingPackApprovalState.Superseded},ct);return null;}
            var previous=data.Deliveries.SingleOrDefault(d=>d.ChatId==chat&&d.Index==index);
            if(previous?.SentAt is not null)continue;
            if(previous?.AttemptedAt is not null){await Review("نتیجهٔ تحویل یکی از عکس‌ها نامشخص است؛ ابتدا پیام‌های مشتری بررسی شوند.");return null;}
            try
            {
                await AssertShippingPackApprovalContentsAsync(data,ct);
            }
            catch(InvalidOperationException e){await Review(e.Message);return null;}
            var slot=index;
            var claimed=false;
            await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>
            {
                if(d.Deliveries.Any(p=>p.ChatId==chat&&p.Index==slot&&p.AttemptedAt.HasValue)||
                    d.State is ShippingPackApprovalState.Dispatched or ShippingPackApprovalState.RevisionRequested or ShippingPackApprovalState.Superseded)return d;
                claimed=true;
                return d with{Deliveries=[..d.Deliveries.Where(p=>p.ChatId!=chat||p.Index!=slot),new(chat,slot,DateTime.UtcNow)]};
            },ct);
            if(!claimed)return 30;
            IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons=slot==data.PhotoFileIds.Length-1?
                [[new("✅ تأیید اقلام برای ارسال",ShippingReferencePrefix+"approve:"+id.ToString("N")),new("✏️ نیاز به اصلاح",ShippingReferencePrefix+"revise:"+id.ToString("N"))]]:[];
            var caption=slot==data.PhotoFileIds.Length-1?ShippingPackApproval.Caption:$"📷 عکس {slot+1} از {data.PhotoFileIds.Length} اقلام بسته";
            var first=data.PreviousApprovalId.HasValue&&slot>=data.PreviousPhotoCount?
                data.Deliveries.SingleOrDefault(d=>d.ChatId==chat&&d.Index==0&&d.SentAt.HasValue)?.MessageId:null;
            var sent=first is >0?await _sender.SendPhotoReplyOnceWithKeyboardAsync(chat,data.PhotoFileIds[slot],$"📷 عکس {slot+1} بسته\n\n"+caption,first.Value,buttons,ct):
                await _sender.SendPhotoOnceWithKeyboardAsync(chat,data.PhotoFileIds[slot],caption,buttons,ct);
            if(sent.IsRetryable)
            {
                await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>d with
                {Deliveries=d.Deliveries.Select(p=>p.ChatId==chat&&p.Index==slot?p with{AttemptedAt=null,Error="Telegram cooldown"}:p).ToArray()},ct);
                return Math.Max(1,sent.RetryAfterSeconds??60);
            }
            if(!sent.IsSuccessful)
            {await Review("تلگرام تحویل عکس را تأیید نکرد؛ عکس‌های بعدی ارسال نشدند.");return null;}
            await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>d with
            {Deliveries=d.Deliveries.Select(p=>p.ChatId==chat&&p.Index==slot?p with{SentAt=DateTime.UtcNow,MessageId=sent.MessageId,Error=null}:p).ToArray()},ct);
        }
        data=await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>d with
        {SentAt=d.SentAt??DateTime.UtcNow,State=d.State is ShippingPackApprovalState.Superseded or ShippingPackApprovalState.RevisionRequested?d.State:d.DispatchedAt.HasValue?ShippingPackApprovalState.Dispatched:d.ConfirmedAt.HasValue?ShippingPackApprovalState.Confirmed:ShippingPackApprovalState.AwaitingCustomer},ct);
        if(data.PreviousApprovalId.HasValue&&data.PreviousPhotoCount>0)
            foreach(var prior in data.Deliveries.Where(d=>d.Index==data.PreviousPhotoCount-1&&d.SentAt.HasValue&&d.MessageId is >0))
            {
                var cleared=await _sender.EditReplyMarkupAsync(prior.ChatId,prior.MessageId!.Value,[],ct);
                if(!cleared.IsSuccessful&&!IsTelegramMessageUnchanged(cleared.Error)&&cleared.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)!=true)
                    throw new InvalidOperationException("پاک‌سازی دکمهٔ تأیید عکس قبلی نیازمند تلاش مجدد است.");
            }
        var notify=false;
        data=await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>
        {if(d.OperatorNoticeAttemptedAt.HasValue)return d;notify=true;return d with{OperatorNoticeAttemptedAt=DateTime.UtcNow};},ct);
        if(notify)await SendShippingPackDeliveryNoticeAsync(id,data,ct);
        await RefreshShippingPackAddressButtonsAsync(data,ct);
        return null;
    }
}
