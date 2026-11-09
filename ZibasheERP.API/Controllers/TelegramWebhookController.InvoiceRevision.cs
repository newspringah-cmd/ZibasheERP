using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal async Task<int?> ProcessInvoiceRevisionAsync(NotificationOutbox job,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==job.Id&&n.Channel==InvoiceRevisionService.Channel&&!n.IsDeleted,ct);
        var data=JsonSerializer.Deserialize<InvoiceRevisionService.Progress>(row.Payload)!;
        if(data.Complete)return null;
        if(data.AwaitTransferOperation is Guid transfer)
        {
            var receipt=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==transfer&&n.Channel==InvoiceTransferService.ReceiptChannel&&!n.IsDeleted,ct);
            var result=receipt is null?null:JsonSerializer.Deserialize<InvoiceTransferService.Result>(receipt.Payload);
            if(result?.SourceInvoiceId!=data.InvoiceId)throw new InvalidOperationException("Partial transfer revision linkage mismatch");
            var follow=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==result.OrderId&&n.Channel==InvoiceTransferService.WorkChannel&&!n.IsDeleted&&n.Payload.Contains(transfer.ToString()),ct);
            if(JsonSerializer.Deserialize<InvoiceTransferService.Progress>(follow.Payload)?.Complete!=true)return 5;
        }

        if(row.CustomerId!=data.CustomerId||row.OrderId!=data.OrderId||row.Recipient!=data.Operation.ToString())throw new InvalidOperationException("Invoice revision job mismatch");
        var invoice=await _db.Invoices.AsNoTracking().SingleAsync(i=>i.Id==data.InvoiceId&&i.OrderId==data.OrderId&&!i.IsDeleted,ct);
        if(invoice.Status==InvoiceStatus.Cancelled||!await _db.Orders.AnyAsync(o=>o.Id==data.OrderId&&o.CustomerId==data.CustomerId&&!o.IsDeleted,ct))
            throw new InvalidOperationException("Invoice revision owner changed");
        var receipts=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==data.OrderId&&n.Channel=="InvoiceMessageReceipt"&&!n.IsDeleted).ToArrayAsync(ct);
        var sources=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==data.OrderId&&n.CustomerId==data.CustomerId&&!n.IsDeleted&&n.Channel=="N8n"&&n.EventType=="InvoiceIssued").ToArrayAsync(ct);
        var valid=sources.Where(n=>
        {
            using var payload=JsonDocument.Parse(n.Payload);return payload.RootElement.TryGetProperty("InvoiceRevisionOperationId",out var id)&&id.TryGetGuid(out var operation)&&operation==data.Operation;
        }).ToDictionary(n=>n.Id);
        var delivered=false;
        foreach(var receipt in receipts.Select(n=>JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(n.Payload)!))
        {
            if(receipt.InvoiceId!=data.InvoiceId||receipt.Kind!="InvoicePdf"||receipt.MessageId<=0||receipt.DeletedAtUtc!=null||!valid.TryGetValue(receipt.SourceEventId,out var source))continue;
            if(!ZibasheERP.Application.Notifications.N8nDeliveryTargetValidator.MatchesTelegramGroup(source.Payload,receipt.ChatId))continue;
            if(await _db.CustomerTelegramGroups.AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==data.CustomerId&&g.ChatId==receipt.ChatId,ct)&&
                !await _db.CustomerTelegramGroups.AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId!=data.CustomerId&&g.ChatId==receipt.ChatId,ct))delivered=true;
        }
        if(!delivered)return 5;
        if(await _db.NotificationOutbox.AnyAsync(n=>data.TelegramEvents.Contains(n.Id)&&!n.IsDeleted&&(n.Status==NotificationOutboxStatus.Pending||n.Status==NotificationOutboxStatus.Processing),ct))return 5;
        await _db.Invoices.Where(i=>i.Id==data.InvoiceId&&!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled).ExecuteUpdateAsync(s=>s
            .SetProperty(i=>i.IsSentToCustomer,true).SetProperty(i=>i.SentToCustomerAt,i=>i.SentToCustomerAt??DateTime.UtcNow)
            .SetProperty(i=>i.DeliveryStatus,InvoiceDeliveryStatus.Delivered).SetProperty(i=>i.DeliveryStatusChangedAt,DateTime.UtcNow)
            .SetProperty(i=>i.DeliveryStatusNote,(string?)null),ct);
        for(var index=data.Deleted;index<data.Cleanup.Length;index++)
        {
            var target=data.Cleanup[index];
            if(!long.TryParse(target.Chat,out var chat)||chat>=0||target.Message<=0)throw new InvalidOperationException("Invoice revision cleanup target invalid");
            InvoiceMessageLifecycle.Receipt? old=null;
            if(target.ReceiptId is Guid receiptId)
            {
                var receipt=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==receiptId&&n.OrderId==data.OrderId&&n.Channel=="InvoiceMessageReceipt"&&n.CreatedAt<=data.CreatedAt,ct);
                old=JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(receipt.Payload)!;
                if(old.InvoiceId!=data.InvoiceId||old.ChatId!=target.Chat||old.MessageId!=target.Message)throw new InvalidOperationException("Invoice revision cleanup receipt invalid");
            }
            if(old?.DeletedAtUtc is null)
            {
                var response=await _sender.DeleteMessageAsync(target.Chat,target.Message,ct);
                if(!response.IsSuccessful&&response.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
                {
                    if(response.Error?.Contains("can't be deleted",StringComparison.OrdinalIgnoreCase)==true||response.Error?.Contains("not enough rights",StringComparison.OrdinalIgnoreCase)==true)
                    {
                        data=await MutateShippingPayloadAsync<InvoiceRevisionService.Progress>(job.Id,InvoiceRevisionService.Channel,p=>p with
                        {Deleted=index+1,ManualCleanup=(p.ManualCleanup??[]).Append(target).DistinctBy(t=>(t.Chat,t.Message)).ToArray()},ct);
                        continue;
                    }
                    throw new InvalidOperationException("حذف نسخهٔ قبلی فاکتور انجام نشد؛ نسخهٔ اصلاح‌شده ارسال شده و پاک‌سازی مجدد تلاش می‌شود.");
                }
                if(target.ReceiptId is Guid id)await MutateShippingPayloadAsync<InvoiceMessageLifecycle.Receipt>(id,"InvoiceMessageReceipt",r=>r with{DeletedAtUtc=DateTime.UtcNow},ct);
            }
            data=await MutateShippingPayloadAsync<InvoiceRevisionService.Progress>(job.Id,InvoiceRevisionService.Channel,p=>p with{Deleted=index+1},ct);
        }
        if(data.ManualCleanup is {Length:>0}&&!string.IsNullOrWhiteSpace(_options.InvoiceFailureChatId))
        {
            var alert=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("invoice-revision-cleanup:"+data.Operation))[..16]);
            if(!await _db.NotificationOutbox.AnyAsync(n=>n.Id==alert,ct))
            {
                var links=data.ManualCleanup.Select(t=>t.Chat.StartsWith("-100",StringComparison.Ordinal)?"https://t.me/c/"+t.Chat[4..]+"/"+t.Message:$"گروه {t.Chat}، پیام {t.Message}");
                _db.NotificationOutbox.Add(new(){Id=alert,CustomerId=data.CustomerId,OrderId=data.OrderId,Channel="Telegram",EventType="InvoiceRevisionCleanupNeedsManualAction",
                    Recipient=_options.InvoiceFailureChatId.Trim(),CreatedAt=DateTime.UtcNow,
                    Payload=JsonSerializer.Serialize(new{Message=$"⚠️ فاکتور {invoice.InvoiceNumber} اصلاح و نسخهٔ جدید با موفقیت برای مشتری ارسال شد.\nتلگرام حذف این پیام‌های نسخهٔ قدیمی را مجاز نمی‌داند؛ لطفاً فقط پیام‌های زیر را دستی حذف کنید. فاکتور دوباره صادر نشود.\n"+string.Join("\n",links)})});
                await _db.SaveChangesAsync(ct);
            }
        }
        await MutateShippingPayloadAsync<InvoiceRevisionService.Progress>(job.Id,InvoiceRevisionService.Channel,p=>p with{Complete=true},ct);
        return null;
    }

    private async Task<string> FormatManualPaymentTrackingWithHistoryAsync(Invoice invoice,bool restocked,CancellationToken ct)
    {
        var text=FormatManualPaymentTrackingMessage(invoice,restocked);
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.CustomerId==invoice.Order!.CustomerId&&n.Channel=="InvoiceCancellationReceipt"&&n.Status==NotificationOutboxStatus.Processed).ToArrayAsync(ct);
        var ids=rows.Select(n=>JsonSerializer.Deserialize<InvoiceCancellationService.Result>(n.Payload)!).Where(r=>r.SourceInvoiceId==invoice.Id).Select(r=>r.InvoiceId).ToArray();
        if(ids.Length==0)return text;
        var cancelled=await _db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(i=>i.SalesList)
            .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(i=>i.Perfume)
            .Where(i=>ids.Contains(i.Id)&&i.Status==InvoiceStatus.Cancelled&&i.Order!.CustomerId==invoice.Order!.CustomerId).OrderBy(i=>i.CreatedAt).ToArrayAsync(ct);
        var published=await InvoiceRestockTracking.PublishedInvoiceIdsAsync(_db,cancelled.Select(i=>i.OrderId).ToArray(),ct);
        return text+"\n\n🔵 آیتم‌های حذف‌شده از این فاکتور:\n"+string.Join("\n\n",cancelled.Select(i=>
            string.Join("\n",i.Order!.Items.Select(x=>$"🔵 {InvoiceRevisionService.Name(x)} — {x.RequestedVolumeMl:0.#} میل — {x.LineTotal:N0} تومان"))+"\n"+
            (published.Contains(i.Id)?InvoiceRestockTracking.Caption:InvoiceRestockTracking.UnknownCaption)));
    }
}
