using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private async Task QueueTrackingAddressNoticeAsync(TrackingDispatch dispatch,CancellationToken ct)
    {
        if(dispatch.CustomerId is not Guid customer||string.IsNullOrWhiteSpace(_options.ShippingChatId))return;
        var chat=_options.ShippingChatId.Trim();
        if(dispatch.ShippingRequestId is Guid request)
        {
            var ids=await _db.OrderItems.AsNoTracking().Where(i=>!i.IsDeleted&&i.ShippingRequestId==request&&i.ShippingTelegramMessageId.HasValue)
                .Select(i=>i.ShippingTelegramMessageId!.Value).Distinct().ToArrayAsync(ct);
            if(ids.Length==1&&await ShippingTrackingNotices.QueueAsync(_db,_options,new(customer,chat,ids[0],request),ct))return;
        }
        var asOf=await _db.TrackingImportBatches.AsNoTracking().Where(b=>b.Id==dispatch.ImportBatchId).Select(b=>b.CreatedAt).SingleAsync(ct);
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="ShippingReferenceContext"&&n.EventType=="AddressReferences"&&
            n.CustomerId==customer&&n.Recipient==chat&&n.CreatedAt<=asOf&&
            !_db.TrackingDispatches.Any(d=>!d.IsDeleted&&d.Id!=dispatch.Id&&d.CustomerId==customer&&d.Status==TrackingDispatchStatus.Sent&&d.SentAt>=n.CreatedAt&&d.SentAt<=asOf))
            .Select(n=>n.Payload).ToArrayAsync(ct);
        var messages=new List<long>();
        foreach(var payload in roots)
        {
            ShippingTrackingNotices.Source? root;try{root=JsonSerializer.Deserialize<ShippingTrackingNotices.Source>(payload);}catch(JsonException){continue;}
            if(root is {Kind:"root",Installed:true,MessageId:>0}&&root.CustomerId==customer&&root.ChatId==chat&&root.RequestId==dispatch.ShippingRequestId&&
                (dispatch.ShippingRequestId.HasValue||root.ItemIds is {Length:0}))messages.Add(root.MessageId.Value);
        }
        var distinct=messages.Distinct().ToArray();
        if(distinct.Length==1)await ShippingTrackingNotices.QueueAsync(_db,_options,new(customer,chat,distinct[0],dispatch.ShippingRequestId),ct);
    }
}
