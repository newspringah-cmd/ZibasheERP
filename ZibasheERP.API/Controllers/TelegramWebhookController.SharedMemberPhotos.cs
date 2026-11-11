using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private const string SharedPhotoControlsChannel="SharedMemberPhotoControls";
    private static Guid SharedPhotoKey(string prefix,Guid root,Guid member)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(prefix+":"+root.ToString("N")+":"+member.ToString("N")))[..16]);

    private async Task<OrderItem[]> LoadSharedMemberPhotoItemsAsync(ShippingReferenceContext context,CancellationToken ct,bool allowClosed=false)
    {
        if(context.SharedPhotoRequestId is not Guid request||context.Kind!="shared-photo"||context.RequestId.HasValue||context.LegacyAddressOnly||
            !context.Installed||context.MessageId is not >0||context.AddressId is null||context.ChatId!=_options.ShippingChatId.Trim())
            throw new InvalidOperationException("مرجع عکسِ عضو پست مشترک معتبر نیست.");
        var parcel=await SharedShippingService.ReadAsync(_db,request,ct)??throw new InvalidOperationException("پست مشترک پیدا نشد.");
        if(!parcel.CustomerIds.Contains(context.CustomerId)||parcel.AddressId!=context.AddressId||!allowClosed&&parcel.ClosedAt.HasValue)
            throw new InvalidOperationException("عضویت یا وضعیت این پست مشترک تغییر کرده است.");
        await ShippingPackageService.ValidatePackageAsync(_db,request,parcel.PrimaryCustomerId,ct);
        if(!await _db.Addresses.AsNoTracking().AnyAsync(a=>a.Id==parcel.AddressId&&a.CustomerId==parcel.PrimaryCustomerId&&!a.IsDeleted,ct))
            throw new InvalidOperationException("آدرس گیرندهٔ اصلی پست مشترک دیگر معتبر نیست.");
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.EventType=="AddressReferences"&&
            n.CustomerId==parcel.PrimaryCustomerId&&n.Recipient==context.ChatId).Select(n=>n.Payload).ToArrayAsync(ct);
        if(!roots.Select(p=>JsonSerializer.Deserialize<ShippingReferenceContext>(p)!).Any(r=>r.Kind=="root"&&r.Installed&&r.CustomerId==parcel.PrimaryCustomerId&&
            r.RequestId==request&&r.AddressId==context.AddressId&&r.MessageId==context.MessageId&&r.ChatId==context.ChatId))
            throw new InvalidOperationException("این دکمه به آدرس اصلی همین پست مشترک متصل نیست.");
        var groups=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==context.CustomerId).Select(g=>g.ChatId).Distinct().Take(2).ToArrayAsync(ct);
        if(groups.Length!=1||!long.TryParse(groups[0],out var chat)||chat>=0||await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==groups[0]&&g.CustomerId!=context.CustomerId,ct))
            throw new InvalidOperationException("گروه حسابداری یکتا و متصل برای این عضو لازم است؛ عکس به گروه شخص دیگری ارسال نمی‌شود.");
        var customer=await _db.Customers.AsNoTracking().SingleAsync(c=>c.Id==context.CustomerId&&!c.IsDeleted,ct);
        return await ShippingPackageService.CustomerItems(_db,customer).AsNoTracking().Include(i=>i.Order).Include(i=>i.SourceSalesListRequest)
            .Where(i=>i.ShippingRequestId==request&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null).OrderBy(i=>i.Id).ToArrayAsync(ct);
    }

    internal async Task<IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>> ExpandSharedMemberPhotoButtonsAsync(
        Guid rootId,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> original,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==rootId&&!n.IsDeleted&&n.Channel==ShippingReferenceChannel,ct);
        if(row is null)return original;
        var root=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
        if(root.Kind!="root"||!root.Installed||root.RequestId is not Guid request||root.MessageId is not >0||root.ChatId!=_options.ShippingChatId.Trim()||root.CustomerId!=row.CustomerId||row.Recipient!=root.ChatId)return original;
        var parcel=await SharedShippingService.ReadAsync(_db,request,ct);
        if(parcel is null||parcel.ClosedAt.HasValue||parcel.PrimaryCustomerId!=root.CustomerId||parcel.AddressId!=root.AddressId)return original;
        var rows=original.Select(r=>(IReadOnlyCollection<TelegramInlineButton>)r.Where(b=>b.CallbackData!=ShippingReferencePrefix+"start:"+rootId.ToString("N")&&
            b.CallbackData!=ShippingReferencePrefix+"restart:"+rootId.ToString("N")).ToArray()).Where(r=>r.Count>0).ToList();
        foreach(var member in parcel.CustomerIds.OrderBy(id=>id==parcel.PrimaryCustomerId?0:1).ThenBy(id=>id))
        {
            var customer=await _db.Customers.AsNoTracking().SingleAsync(c=>c.Id==member&&!c.IsDeleted,ct);
            var items=await ShippingPackageService.CustomerItems(_db,customer).AsNoTracking().Where(i=>i.ShippingRequestId==request&&
                i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null).OrderBy(i=>i.Id).ToArrayAsync(ct);
            var id=SharedPhotoKey("shared-photo",rootId,member);
            var context=new ShippingReferenceContext(member,null,items.Select(i=>i.Id).ToArray(),root.ChatId,Kind:"shared-photo",MessageId:root.MessageId,
                Installed:true,AddressId:root.AddressId,SharedPhotoRequestId:request);
            var payload=JsonSerializer.Serialize(context);
            var existing=await _db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==id,ct);
            if(existing is null)_db.NotificationOutbox.Add(new(){Id=id,CustomerId=member,Channel=ShippingReferenceChannel,EventType="SharedMemberPhotoReference",Recipient=root.ChatId,
                Payload=payload,Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});
            else
            {
                if(existing.CustomerId!=member||existing.Channel!=ShippingReferenceChannel||existing.Recipient!=root.ChatId)throw new InvalidOperationException("مرجع عکس عضو تغییر هویت داده است.");
                if(existing.Payload!=payload){existing.Payload=payload;existing.UpdatedAt=DateTime.UtcNow;}existing.IsDeleted=false;
            }
            var previous=await FindShippingPackAppendBaseAsync(context,items,ct);
            var label=OrderCustomerLabel(customer);if(label.Length>40)label=label[..39]+"…";
            rows.Add([new((previous is null?"📷 عکس تأیید":ShippingPackNextPhotoLabel(previous.Data.PhotoFileIds.Length))+" "+label,ShippingReferencePrefix+"start:"+id.ToString("N"))]);
        }
        foreach(var name in parcel.LegacyUsernames??[])rows.Add([new("📷 عکس تأیید @"+name,SharedShippingPrefix+"photo-unlinked")]);
        await _db.SaveChangesAsync(ct);return rows;
    }

    internal async Task RecoverSharedMemberPhotoControlsAsync(CancellationToken ct,Guid? selectedRequest=null)
    {
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.EventType=="AddressReferences"&&n.Recipient==_options.ShippingChatId.Trim()).ToArrayAsync(ct);
        foreach(var row in roots)
        {
            try
            {
            var root=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
            if(root.Kind!="root"||!root.Installed||root.MessageId is not >0||root.RequestId is not Guid request||selectedRequest.HasValue&&request!=selectedRequest)continue;
            var parcel=await SharedShippingService.ReadAsync(_db,request,ct);if(parcel is null||parcel.ClosedAt.HasValue)continue;
            var controls=new List<IReadOnlyCollection<TelegramInlineButton>>{new[]{new TelegramInlineButton("📦 اقلام پست مشترک",SharedShippingPrefix+"items:"+request.ToString("N"))}};
            controls.AddRange(ShippingParcelButtons(request));controls.AddRange(ShippingReferenceButtons(row.Id));
            var expanded=await ExpandSharedMemberPhotoButtonsAsync(row.Id,controls,ct);
            var signature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expanded))));
            var id=SharedPhotoKey("shared-photo-controls",row.Id,root.CustomerId);
            var marker=await _db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==id,ct);
            if(marker is not null&&(marker.NextAttemptAt>DateTime.UtcNow||marker.Payload==signature&&marker.Status==NotificationOutboxStatus.Processed))continue;
            if(marker is null){marker=new(){Id=id,CustomerId=root.CustomerId,Channel=SharedPhotoControlsChannel,EventType="MemberPhotoButtons",Recipient=root.ChatId,CreatedAt=DateTime.UtcNow};_db.NotificationOutbox.Add(marker);}
            var result=await _sender.EditReplyMarkupAsync(root.ChatId,root.MessageId.Value,expanded,ct);marker.Payload=signature;marker.UpdatedAt=DateTime.UtcNow;
            if(result.IsSuccessful||IsTelegramMessageUnchanged(result.Error)||result.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)==true)
            {marker.Status=NotificationOutboxStatus.Processed;marker.ProcessedAt=DateTime.UtcNow;marker.LastError=null;marker.NextAttemptAt=null;}
            else{marker.Status=NotificationOutboxStatus.Pending;marker.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Max(30,result.RetryAfterSeconds??30)+1);marker.LastError="Shared photo controls require retry";}
            await _db.SaveChangesAsync(ct);if(result.IsRetryable)break;
            }
            catch(Exception error)when(!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Shared member photo controls need review: {ErrorType}",error.GetType().Name);
                _db.ChangeTracker.Clear();
            }
        }
    }
}
