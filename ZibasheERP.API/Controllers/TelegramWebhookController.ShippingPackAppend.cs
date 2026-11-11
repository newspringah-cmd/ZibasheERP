using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed record ShippingPackAppendBase(Guid Id,ShippingPackApprovalData Data);
    private async Task<ShippingPackAppendBase?> FindShippingPackAppendBaseAsync(ShippingReferenceContext context,OrderItem[] items,CancellationToken ct)
    {
        var scope=ShippingPackApprovalScope(new(){CustomerId=context.CustomerId,RequestId=context.RequestId,LegacyAddressOnly=context.LegacyAddressOnly,
            AddressId=context.AddressId,AddressMessageId=context.MessageId});
        var row=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==context.CustomerId&&n.Recipient==scope)
            .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).FirstOrDefaultAsync(ct);
        if(row is null)return null;
        var data=JsonSerializer.Deserialize<ShippingPackApprovalData>(row.Payload)!;
        if(data.State is not(ShippingPackApprovalState.AwaitingCustomer or ShippingPackApprovalState.Confirmed)||!ShippingPackPhotosDelivered(data)||
            data.DispatchedAt.HasValue||data.RequestId!=context.RequestId||data.LegacyAddressOnly!=context.LegacyAddressOnly||
            data.ShippingChatId!=context.ChatId||!data.ItemIds.ToHashSet().IsSubsetOf(items.Select(i=>i.Id)))return null;
        var groups=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==context.CustomerId).Select(g=>g.ChatId).Distinct().ToArrayAsync(ct);
        if(!groups.ToHashSet().SetEquals(data.Deliveries.Select(d=>d.ChatId)))return null;
        return new(row.Id,data);
    }
    private static string ShippingPackNextPhotoLabel(int count)=>count switch
    {1=>"📷 ارسال عکس دوم",2=>"📷 ارسال عکس سوم",3=>"📷 ارسال عکس چهارم",4=>"📷 ارسال عکس پنجم",_=>$"📷 ارسال عکس {count+1}"};
    private static IReadOnlyCollection<TelegramInlineButton>[] ShippingReferenceButtonsWithPhotoCount(Guid id,int count)
    {
        var rows=ShippingReferenceButtons(id).Select(r=>(IReadOnlyCollection<TelegramInlineButton>)r.Select(b=>b.CallbackData==ShippingReferencePrefix+"start:"+id.ToString("N")?
            new TelegramInlineButton(ShippingPackNextPhotoLabel(count),b.CallbackData):b).ToArray()).ToList();
        if(count>0)rows.Add([new("🔄 ارسال مجموعهٔ اصلاح‌شده",ShippingReferencePrefix+"restart:"+id.ToString("N"))]);
        return rows.ToArray();
    }
    private async Task RefreshShippingPackAddressButtonsAsync(ShippingPackApprovalData data,CancellationToken ct)
    {
        if(!ShippingPackPhotosDelivered(data)||data.State is not(ShippingPackApprovalState.AwaitingCustomer or ShippingPackApprovalState.Confirmed))return;
        var scope=ShippingPackApprovalScope(data);
        var latest=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==data.CustomerId&&n.Recipient==scope)
            .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Payload).FirstAsync(ct);
        var current=JsonSerializer.Deserialize<ShippingPackApprovalData>(latest)!;
        if(!ShippingPackPhotosDelivered(current)||!current.PhotoFileIds.SequenceEqual(data.PhotoFileIds))return;
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.EventType=="AddressReferences"&&
            n.CustomerId==data.CustomerId&&n.Recipient==data.ShippingChatId).ToArrayAsync(ct);
        foreach(var row in roots)
        {
            var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
            if(context.Kind!="root"||!context.Installed||context.MessageId is not >0||context.ChatId!=data.ShippingChatId||context.CustomerId!=data.CustomerId||
                (data.LegacyAddressOnly?context.AddressId!=data.AddressId||context.MessageId!=data.AddressMessageId:context.RequestId!=data.RequestId))continue;
            if(!data.LegacyAddressOnly&&data.RequestId is null&&!context.ItemIds.ToHashSet().SetEquals(data.ItemIds))continue;
            var manual=(await ManualShippingAddresses.SourcesAsync(_db,_options,ct)).SingleOrDefault(s=>s.ReferenceId==row.Id);
            if(manual is not null)
            {
                var manualRows=await ManualAddressButtonsAsync(manual,await ManualShippingAddresses.IsSentAsync(_db,manual,ct),ct);
                var manualResult=await _sender.EditReplyMarkupAsync(context.ChatId,context.MessageId.Value,manualRows,ct);
                if(!manualResult.IsSuccessful&&!IsTelegramMessageUnchanged(manualResult.Error)&&manualResult.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)!=true)
                    throw new InvalidOperationException("به‌روزرسانی دکمه‌های آدرس دستی نیازمند تلاش مجدد است.");
                continue;
            }
            var rows=new List<IReadOnlyCollection<TelegramInlineButton>>();
            if(context.RequestId.HasValue)
            {
                if(await SharedShippingService.ReadAsync(_db,context.RequestId.Value,ct)is not null)
                    rows.Add([new("📦 اقلام پست مشترک",SharedShippingPrefix+"items:"+context.RequestId.Value.ToString("N"))]);
                rows.AddRange(ShippingParcelButtons(context.RequestId.Value));
            }
            else rows.Add([new("📸 ارسال کد رهگیری","shipping:trackingcustomer:"+context.CustomerId.ToString("N"))]);
            rows.AddRange(ShippingReferenceButtonsWithPhotoCount(row.Id,data.PhotoFileIds.Length));
            var result=await _sender.EditReplyMarkupAsync(context.ChatId,context.MessageId.Value,rows,ct);
            if(!result.IsSuccessful&&!IsTelegramMessageUnchanged(result.Error)&&result.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)!=true)
                throw new InvalidOperationException("به‌روزرسانی دکمهٔ عکس بعدی نیازمند تلاش مجدد است.");
        }
    }
}
