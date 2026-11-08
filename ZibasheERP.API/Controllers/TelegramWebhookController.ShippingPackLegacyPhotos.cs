using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    // Legacy stock may have a real address and accounting group without ERP invoices.
    // This is a photo-only scope: it must never manufacture order or shipment items.
    private async Task AssertLegacyPackAddressAsync(ShippingReferenceContext context,CancellationToken ct)
    {
        if(!context.LegacyAddressOnly||context.Kind!="root"||context.RequestId.HasValue||context.ItemIds.Length!=0||
            context.AddressId is null||context.MessageId is not >0||!context.Installed||context.ChatId!=_options.ShippingChatId.Trim()||
            !await _db.Addresses.AsNoTracking().AnyAsync(a=>a.Id==context.AddressId.Value&&a.CustomerId==context.CustomerId&&!a.IsDeleted,ct))
            throw new InvalidOperationException("آدرس معتبر همین مشتری برای ارسال عکس لازم است؛ از دکمهٔ زیر آدرس خودش استفاده کنید.");
        var roots=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&
            n.EventType=="AddressReferences"&&n.CustomerId==context.CustomerId&&n.Recipient==context.ChatId)
            .Select(n=>n.Payload).ToArrayAsync(ct);
        var valid=roots.Select(p=>JsonSerializer.Deserialize<ShippingReferenceContext>(p)!).Any(r=>r.Kind=="root"&&r.Installed&&
            r.ChatId==context.ChatId&&r.CustomerId==context.CustomerId&&r.AddressId==context.AddressId&&r.MessageId==context.MessageId&&
            r.RequestId is null&&r.ItemIds.Length==0);
        if(!valid)throw new InvalidOperationException("مرجع آدرس این بسته قابل تأیید نیست؛ آدرس را دوباره از منو ثبت کنید.");
    }

    private async Task AssertShippingPackApprovalContentsAsync(ShippingPackApprovalData data,CancellationToken ct)
    {
        if(data.LegacyAddressOnly)
        {
            await AssertLegacyPackAddressAsync(new(data.CustomerId,data.RequestId,data.ItemIds,data.ShippingChatId,
                MessageId:data.AddressMessageId,Installed:true,AddressId:data.AddressId,LegacyAddressOnly:true),ct);
            return;
        }
        var current=await LoadShippingReferenceItemsAsync(new(data.CustomerId,data.RequestId,data.ItemIds,data.ShippingChatId),ct);
        if(!current.Select(i=>i.Id).ToHashSet().SetEquals(data.ItemIds))
            throw new InvalidOperationException("اقلام بسته تغییر کرده؛ مسئول ارسال باید عکس جدید بفرستد.");
    }

    private static string ShippingPackApprovalScope(ShippingPackApprovalData data)=>data.LegacyAddressOnly?
        $"legacy-address:{data.AddressId:N}:{data.AddressMessageId}":data.RequestId?.ToString("N")??"customer";
}
