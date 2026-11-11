using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    internal async Task<int?> ProcessShippingPackResponseAsync(NotificationOutbox job,CancellationToken ct)
    {
        async Task<ShippingPackResponseData> Read()=>JsonSerializer.Deserialize<ShippingPackResponseData>((await _db.NotificationOutbox.AsNoTracking()
            .SingleAsync(n=>n.Id==job.Id&&n.Channel==ShippingPackResponseChannel&&!n.IsDeleted,ct)).Payload)!;
        async Task<(ShippingPackApprovalData Approval,Customer Customer)?> Validate(ShippingPackResponseData data)
        {
            var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==data.ApprovalId&&n.Channel==ShippingPackApproval.ReceiptChannel&&!n.IsDeleted,ct);
            if(row is null||row.CustomerId!=job.CustomerId)return null;
            var approval=JsonSerializer.Deserialize<ShippingPackApprovalData>(row.Payload)!;
            if(approval.CustomerId!=job.CustomerId||approval.ShippingChatId!=_options.ShippingChatId.Trim())return null;
            var latest=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==job.CustomerId&&n.Recipient==row.Recipient)
                .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Id).FirstAsync(ct);
            if(latest!=data.ApprovalId)return null;
            if(data.Kind=="confirmed")
            {
                if(approval.ConfirmationResponseId!=job.Id||approval.ConfirmedBy!=data.ActorId||!approval.ConfirmedAt.HasValue||approval.State is not (ShippingPackApprovalState.Confirmed or ShippingPackApprovalState.Dispatched))return null;
            }
            else if(data.Kind!="revision"||approval.RevisionResponseId!=job.Id||approval.State is not (ShippingPackApprovalState.RevisionRequested or ShippingPackApprovalState.Dispatched)||
                !approval.RevisionSubmittedAt.HasValue||approval.RevisionText!=data.RevisionText)return null;
            if(approval.SharedPhotoRequestId.HasValue)
            {
                try{await LoadSharedMemberPhotoItemsAsync(new(approval.CustomerId,null,approval.ItemIds,approval.ShippingChatId,Kind:"shared-photo",
                    MessageId:approval.AddressMessageId,Installed:true,AddressId:approval.AddressId,SharedPhotoRequestId:approval.SharedPhotoRequestId),ct,true);}
                catch(InvalidOperationException){return null;}
            }
            if(approval.RequestId.HasValue)
            {
                try{await ShippingPackageService.ValidatePackageAsync(_db,approval.RequestId.Value,approval.CustomerId,ct);}
                catch(InvalidOperationException){return null;}
            }
            var customer=await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==job.CustomerId&&!c.IsDeleted,ct);
            if(!await IsShippingPackApprovalActorAsync(customer,new(data.ActorId,data.ActorUsername),data.CustomerChatId,data.Kind=="confirmed"&&data.StaffApproval,ct))return null;
            if(!approval.Deliveries.Any(d=>d.ChatId==data.CustomerChatId&&d.Index==approval.PhotoFileIds.Length-1&&d.MessageId==data.PhotoMessageId&&d.SentAt.HasValue))return null;
            return(approval,customer!);
        }
        var data=await Read();
        foreach(var stage in data.Kind=="revision"?new[]{"operator","customer"}:new[]{"customer","operator"})
        {
            data=await Read();if(stage=="operator"&&!data.NotifyOperator)continue;
            var current=await Validate(data);if(current is null)return null;
            var previous=stage=="customer"?data.Customer:data.Operator;
            if(previous?.SentAt is not null)continue;
            if(previous?.AttemptedAt is not null)
            {
                _logger.LogWarning("Shipping pack feedback delivery needs review: job {JobId}, stage {Stage}",job.Id,stage);
                return null; // Unknown delivery after restart is never repeated.
            }
            var claimed=false;
            await MutateShippingPayloadAsync<ShippingPackResponseData>(job.Id,ShippingPackResponseChannel,d=>
            {
                var slot=stage=="customer"?d.Customer:d.Operator;if(slot?.AttemptedAt is not null)return d;
                claimed=true;var attempt=new ShippingPackResponseAttempt(DateTime.UtcNow);
                return stage=="customer"?d with{Customer=attempt}:d with{Operator=attempt};
            },ct);
            if(!claimed)return 30;
            TelegramSendResult sent;
            if(stage=="customer")sent=await _sender.SendReplyOnceAsync(data.CustomerChatId,
                data.Kind=="confirmed"?ShippingPackConfirmedText+(data.StaffApproval?"\nثبت تأیید توسط "+ShippingPackStaffLabel(data.ActorId,data.ActorUsername):""):ShippingPackReviewSentText,data.PhotoMessageId,ct);
            else
            {
                var itemSummary=current.Value.Approval.LegacyAddressOnly?"اقلام ثبت‌شدهٔ قبلی":$"تعداد آیتم‌ها: {current.Value.Approval.ItemIds.Length}";
                var text=data.Kind=="confirmed"?$"{(data.StaffApproval?"✅ تأیید مدیر/ادمین برای ارسال":"✅ تأیید مشتری برای ارسال")}\n{OrderCustomerLabel(current.Value.Customer)}\n{itemSummary}"+
                    (data.StaffApproval?"\nثبت تأیید توسط "+ShippingPackStaffLabel(data.ActorId,data.ActorUsername):""):
                    $"✏️ درخواست اصلاح عکس بسته\n{OrderCustomerLabel(current.Value.Customer)}\n{itemSummary}\n\nتوضیحات اصلاح:\n{data.RevisionText}";
                var request=current.Value.Approval.RequestId;
                var addresses=request.HasValue?await _db.OrderItems.AsNoTracking().Where(i=>!i.IsDeleted&&i.ShippingRequestId==request&&i.ShippingTelegramMessageId.HasValue)
                    .Select(i=>i.ShippingTelegramMessageId!.Value).Distinct().Take(2).ToArrayAsync(ct):[];
                if((current.Value.Approval.LegacyAddressOnly||current.Value.Approval.SharedPhotoRequestId.HasValue)&&current.Value.Approval.AddressMessageId is long legacyAddress)addresses=[legacyAddress];
                sent=addresses.Length==1?await _sender.SendReplyOnceAsync(current.Value.Approval.ShippingChatId,text,addresses[0],ct)
                    :await _sender.SendKeyboardDismissAsync(current.Value.Approval.ShippingChatId,text,ct);
            }
            await MutateShippingPayloadAsync<ShippingPackResponseData>(job.Id,ShippingPackResponseChannel,d=>
            {
                var slot=(stage=="customer"?d.Customer:d.Operator)!;
                var next=sent.IsSuccessful?slot with{SentAt=DateTime.UtcNow,MessageId=sent.MessageId,Error=null}:
                    sent.IsRetryable?slot with{AttemptedAt=null,Error="Telegram cooldown"}:slot with{Error="Delivery needs review"};
                return stage=="customer"?d with{Customer=next}:d with{Operator=next};
            },ct);
            if(sent.IsRetryable)return Math.Max(1,sent.RetryAfterSeconds??30);
            if(!sent.IsSuccessful)
            {_logger.LogWarning("Shipping pack feedback delivery needs review: job {JobId}, stage {Stage}",job.Id,stage);return null;}
        }
        return null;
    }
}
