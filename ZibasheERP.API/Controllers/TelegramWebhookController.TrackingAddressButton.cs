using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryMarkDeliveredTrackingButtonAsync(Guid customer,string chat,long source,CancellationToken ct)
    {
        var id=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"tracking-address-notice:{chat}:{source}"))[..16]);
        var notice=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Channel==ShippingTrackingNotices.Channel&&!n.IsDeleted,ct);
        if(notice is null)return false;
        var data=JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(notice.Payload)!;
        if(data.CustomerId!=customer||data.ChatId!=chat||data.SourceMessageId!=source)throw new InvalidOperationException("Tracking button notice identity mismatch");
        await MarkTrackingAddressButtonAsync(notice,data,ct);return true;
    }
    internal async Task MarkTrackingAddressButtonAsync(NotificationOutbox notice,ShippingTrackingNotices.Data data,CancellationToken ct)
    {
        if(notice.Channel!=ShippingTrackingNotices.Channel||notice.CustomerId!=data.CustomerId||notice.Recipient!=data.ChatId||
            data.ChatId!=_options.ShippingChatId.Trim()||!await _db.NotificationOutbox.AsNoTracking().AnyAsync(n=>n.Id==notice.Id&&n.Channel==ShippingTrackingNotices.Channel&&!n.IsDeleted,ct)||
            !await ShippingTrackingNotices.SourceMatchesAsync(_db,data,ct))throw new InvalidOperationException("Tracking button address scope mismatch");
        var manualSources=await ManualShippingAddresses.SourcesAsync(_db,_options,ct);
        var manual=manualSources.SingleOrDefault(s=>s.Root.CustomerId==data.CustomerId&&s.Root.MessageId==data.SourceMessageId&&s.Root.ChatId==data.ChatId);
        if(manual is not null)
        {
            await ManualShippingAddresses.CompleteAsync(_db,manual,null,"acknowledged-tracking-delivery",ct);
            var manualRows=await ManualAddressButtonsAsync(manual,true,ct);
            var updated=await _sender.EditReplyMarkupAsync(data.ChatId,data.SourceMessageId,manualRows,ct);
            if(!updated.IsSuccessful&&!IsTelegramMessageUnchanged(updated.Error))
            {
                if(updated.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)==true)throw new ShippingTrackingNotices.AddressMessageMissingException();
                throw new ShippingTrackingNotices.ButtonRetryException(Math.Max(30,updated.RetryAfterSeconds??30));
            }
            return;
        }
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.EventType=="AddressReferences"&&n.CustomerId==data.CustomerId&&n.Recipient==data.ChatId)
            .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
        var matches=roots.Select(n=>(Row:n,Root:JsonSerializer.Deserialize<ShippingReferenceContext>(n.Payload)!))
            .Where(v=>v.Root.Kind=="root"&&v.Root.Installed&&v.Root.MessageId==data.SourceMessageId&&v.Root.CustomerId==data.CustomerId&&v.Root.ChatId==data.ChatId&&(!data.RequestId.HasValue||v.Root.RequestId==data.RequestId)).ToArray();
        if(matches.Length>1)throw new InvalidOperationException("Tracking button address root is ambiguous");
        var rows=new List<IReadOnlyCollection<TelegramInlineButton>>();
        var request=matches.Length==1?matches[0].Root.RequestId:data.RequestId;
        if(request is Guid parcel)
        {
            if(await SharedShippingService.ReadAsync(_db,parcel,ct)is not null)rows.Add([new("📦 اقلام پست مشترک",SharedShippingPrefix+"items:"+parcel.ToString("N"))]);
            rows.AddRange(ShippingParcelButtons(parcel).Select(row=>(IReadOnlyCollection<TelegramInlineButton>)row.Select(b=>
                b.CallbackData=="shipping:sent:"+parcel.ToString("N")?new("✅ ارسال شد","shipping:trackingdone"):
                b.CallbackData=="shipping:trackingbatch:"+parcel.ToString("N")?new TelegramInlineButton(ShippingTrackingNotices.Text,"shipping:trackingdone",Style:"success"):b).ToArray()));
        }
        else rows.Add([new(ShippingTrackingNotices.Text,"shipping:trackingdone",Style:"success")]);
        if(matches.Length==1)
        {
            var receipts=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.CustomerId==data.CustomerId&&n.Channel==ShippingPackApproval.ReceiptChannel)
                .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Payload).ToArrayAsync(ct);
            var last=receipts.Select(p=>JsonSerializer.Deserialize<ShippingPackApprovalData>(p)!).FirstOrDefault(p=>p.CustomerId==data.CustomerId&&p.ShippingChatId==data.ChatId&&
                p.AddressMessageId==data.SourceMessageId&&p.RequestId==request&&ShippingPackPhotosDelivered(p));
            rows.AddRange(last is null?ShippingReferenceButtons(matches[0].Row.Id):ShippingReferenceButtonsWithPhotoCount(matches[0].Row.Id,last.PhotoFileIds.Length));
        }
        var sent=await _sender.EditReplyMarkupAsync(data.ChatId,data.SourceMessageId,rows,ct);
        if(!sent.IsSuccessful&&!IsTelegramMessageUnchanged(sent.Error))
        {
            if(sent.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)==true)throw new ShippingTrackingNotices.AddressMessageMissingException();
            throw new ShippingTrackingNotices.ButtonRetryException(sent.IsRetryable?Math.Max(1,sent.RetryAfterSeconds??60):30);
        }
    }
}
