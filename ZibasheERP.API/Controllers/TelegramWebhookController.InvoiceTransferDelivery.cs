using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal async Task<int?> ProcessInvoiceTransferFollowupAsync(NotificationOutbox job,CancellationToken ct)
    {
        var work=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==job.Id&&n.Channel==InvoiceTransferService.WorkChannel&&!n.IsDeleted,ct);
        var progress=JsonSerializer.Deserialize<InvoiceTransferService.Progress>(work.Payload)!;
        var receipt=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==progress.Operation&&n.Channel==InvoiceTransferService.ReceiptChannel&&!n.IsDeleted,ct);
        var data=JsonSerializer.Deserialize<InvoiceTransferService.Result>(receipt.Payload)!;
        if(work.OrderId!=data.OrderId||work.CustomerId!=data.ToId||receipt.OrderId!=data.OrderId||receipt.CustomerId!=data.ToId)
            throw new InvalidOperationException("Invoice transfer receipt mismatch");
        if(progress.Complete)return null;
        var invoice=await _db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Customer).Include(i=>i.Order).ThenInclude(o=>o!.Items)
            .SingleAsync(i=>i.Id==data.InvoiceId&&!i.IsDeleted,ct);
        if(invoice.Order!.CustomerId!=data.ToId||invoice.Status==InvoiceStatus.Cancelled)throw new InvalidOperationException("Invoice transfer owner changed");
        async Task Save(InvoiceTransferService.Progress p)
        {progress=await MutateShippingPayloadAsync<InvoiceTransferService.Progress>(job.Id,InvoiceTransferService.WorkChannel,_=>p,ct);}
        if(!progress.ReportUpdated&&!progress.ReportMissing)
        {
            var reportsEdited=0;
            async Task Edit(string? chat,long? message,string text,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons)
            {
                if(string.IsNullOrWhiteSpace(chat)||message is not >0)return;
                if(chat.Trim()!=_options.NewPaymentsChatId.Trim())throw new InvalidOperationException("Invoice transfer report destination mismatch");
                var response=await _sender.EditTextWithKeyboardAsync(chat,message.Value,text,buttons,ct);
                if(!response.IsSuccessful&&!IsTelegramMessageUnchanged(response.Error))throw new InvalidOperationException("Invoice transfer report retry required");
                reportsEdited++;
            }
            if(invoice.Order.InvoiceIssuanceBatchId is Guid batch)
            {
                var listIds=invoice.Order.Items.Where(x=>!x.IsDeleted).Select(x=>x.SalesListId).ToHashSet();
                foreach(var report in await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(batch,ct))
                    if(listIds.Contains(report.SalesListId))await Edit(report.TelegramChatId,report.TelegramMessageId,report.Message,BuildPaymentTrackingButtons(report));
            }
            else await Edit(invoice.TelegramPaymentTrackingChatId,invoice.TelegramPaymentTrackingMessageId,FormatManualPaymentTrackingMessage(invoice),BuildManualPaymentTrackingButtons(invoice));
            if(reportsEdited==0&&DateTime.UtcNow-data.CreatedAt<TimeSpan.FromMinutes(2))return 5;
            await Save(progress with{ReportUpdated=reportsEdited>0,ReportMissing=reportsEdited==0,Error=reportsEdited==0?"شناسهٔ گزارش واریز قبلی ثبت نشده؛ اصلاح همان گزارش نیازمند بررسی حسابداری است.":null});
        }
        var pdfReceipts=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==data.OrderId&&n.Channel=="InvoiceMessageReceipt"&&!n.IsDeleted).ToArrayAsync(ct);
        var pdfSources=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==data.OrderId&&n.CustomerId==data.ToId&&n.Channel=="N8n"&&n.EventType=="InvoiceIssued"&&!n.IsDeleted).Select(n=>new{n.Id,n.Payload}).ToArrayAsync(ct);
        var validSources=pdfSources.Where(n=>
        {
            using var body=JsonDocument.Parse(n.Payload);
            return body.RootElement.TryGetProperty("TransferOperationId",out var operation)&&operation.TryGetGuid(out var op)&&op==progress.Operation&&
                ZibasheERP.Application.Notifications.N8nDeliveryTargetValidator.MatchesTelegramGroup(n.Payload,data.ToChat);
        }).Select(n=>n.Id).ToHashSet();
        var delivered=pdfReceipts.Select(n=>JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(n.Payload)!).Any(r=>
            r.InvoiceId==data.InvoiceId&&validSources.Contains(r.SourceEventId)&&r.ChatId==data.ToChat&&r.Kind=="InvoicePdf"&&r.MessageId>0&&r.DeletedAtUtc is null);
        if(!delivered)return 5;
        if(await _db.NotificationOutbox.AnyAsync(n=>data.TelegramEvents.Contains(n.Id)&&!n.IsDeleted&&(n.Status==NotificationOutboxStatus.Pending||n.Status==NotificationOutboxStatus.Processing),ct))return 5;
        await _db.Invoices.Where(i=>i.Id==data.InvoiceId&&!i.IsDeleted&&i.Status!=InvoiceStatus.Cancelled).ExecuteUpdateAsync(s=>s
            .SetProperty(i=>i.IsSentToCustomer,true).SetProperty(i=>i.SentToCustomerAt,i=>i.SentToCustomerAt??DateTime.UtcNow)
            .SetProperty(i=>i.DeliveryStatus,InvoiceDeliveryStatus.Delivered).SetProperty(i=>i.DeliveryStatusChangedAt,DateTime.UtcNow).SetProperty(i=>i.DeliveryStatusNote,(string?)null),ct);
        for(var index=progress.Deleted;index<data.Cleanup.Length;index++)
        {
            var target=data.Cleanup[index];
            if(!long.TryParse(target.Chat,out var chat)||chat>=0||target.Message<=0)throw new InvalidOperationException("Invalid transfer cleanup target");
            InvoiceMessageLifecycle.Receipt? recorded=null;
            if(target.ReceiptId is Guid recordId)
            {
                var record=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==recordId&&n.Channel=="InvoiceMessageReceipt"&&n.OrderId==data.OrderId,ct);
                recorded=JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(record.Payload)!;
                if(recorded.InvoiceId!=data.InvoiceId||recorded.ChatId!=target.Chat||recorded.MessageId!=target.Message||record.CreatedAt>data.CreatedAt)
                    throw new InvalidOperationException("Transfer cleanup receipt mismatch");
            }
            if(recorded?.DeletedAtUtc is null)
            {
                var response=await _sender.DeleteMessageAsync(target.Chat,target.Message,ct);
                if(!response.IsSuccessful&&response.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
                {
                    var restricted=response.Error?.Contains("can't be deleted",StringComparison.OrdinalIgnoreCase)==true||response.Error?.Contains("not enough rights",StringComparison.OrdinalIgnoreCase)==true;
                    if(restricted)
                    {
                        await Save(progress with{Error="تلگرام حذف یکی از پیام‌های قدیمی را مجاز نمی‌داند؛ همان پیام را دستی حذف و «تلاش مجدد پاک‌سازی» را بزنید."});
                        throw new InvalidOperationException("InvoiceTransferManualCleanup");
                    }
                    throw new InvalidOperationException("Invoice transfer cleanup retry required");
                }
                if(target.ReceiptId is Guid receiptId)await MutateShippingPayloadAsync<InvoiceMessageLifecycle.Receipt>(receiptId,"InvoiceMessageReceipt",r=>r with{DeletedAtUtc=DateTime.UtcNow},ct);
            }
            await Save(progress with{Deleted=index+1});
        }
        var incompletePhotos=await _db.NotificationOutbox.CountAsync(n=>data.TelegramEvents.Contains(n.Id)&&n.Status==NotificationOutboxStatus.Failed,ct);
        var issues=new List<string>();if(progress.ReportMissing)issues.Add("شناسهٔ گزارش واریز قبلی ثبت نشده؛ اصلاح همان گزارش نیازمند بررسی حسابداری است.");
        if(data.NeedsManualSourceCleanup)issues.Add("بعضی پیام‌های قدیمی مبدا شناسهٔ ثبت‌شده ندارند و حذف دستی لازم دارند.");
        if(incompletePhotos>0)issues.Add("PDF تحویل شد؛ ارسال بعضی عکس‌ها/پیام‌های همراه نیازمند بررسی است.");
        await Save(progress with{Complete=true,Error=issues.Count==0?null:string.Join("\n",issues)});
        return null;
    }
}
