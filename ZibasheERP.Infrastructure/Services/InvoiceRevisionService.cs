using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public static class InvoiceRevisionService
{
    public const string Channel="InvoiceRevisionFollowup";
    public sealed record Target(string Chat,long Message,Guid? ReceiptId);
    public sealed record Progress(Guid Operation,Guid InvoiceId,Guid OrderId,Guid CustomerId,Guid DeliveryEvent,Guid[] TelegramEvents,
        Target[] Cleanup,DateTime CreatedAt,int Deleted=0,bool Complete=false,Target[]? ManualCleanup=null);

    // The retained invoice keeps its identity. Selected lines move to a linked,
    // cancelled accounting document, so stock-return proof and payment history
    // remain exact without hiding the retained lines or cancelling their requests.
    public static async Task<Invoice> SplitAsync(AppDbContext db,Invoice invoice,Guid[] selected,Guid operation,DateTime now,CancellationToken ct)
    {
        var order=invoice.Order!;var removed=order.Items.Where(x=>!x.IsDeleted&&selected.Contains(x.Id)).ToArray();
        var retained=order.Items.Where(x=>!x.IsDeleted&&!selected.Contains(x.Id)).ToArray();
        if(removed.Length!=selected.Length||retained.Length==0)throw new InvalidOperationException("انتخاب آیتم‌ها معتبر نیست.");
        var events=await db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE OrderId={order.Id} AND IsDeleted=0 AND Channel IN(N'Telegram',N'N8n')").ToArrayAsync(ct);
        if(events.Any(n=>n.Status==NotificationOutboxStatus.Processing&&n.LockedUntil>now))throw new InvalidOperationException("ارسال فاکتور در حال انجام است؛ کمی بعد دوباره تلاش کنید.");
        var oldOwnerPayload=events.Where(n=>n.CustomerId==order.CustomerId&&n.Channel=="N8n"&&n.EventType=="InvoiceIssued").OrderByDescending(n=>n.CreatedAt).Select(n=>n.Payload).FirstOrDefault();
        UpdateGiftNotifications(db,order,removed,retained,events,now);
        var records=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==order.Id&&!n.IsDeleted&&n.Channel=="InvoiceMessageReceipt").ToArrayAsync(ct);
        var ownerSources=events.Where(n=>n.CustomerId==order.CustomerId).Select(n=>n.Id).ToHashSet();
        var cleanup=records.Select(n=>(Row:n,Data:JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(n.Payload)!))
            .Where(x=>x.Data.InvoiceId==invoice.Id&&x.Data.DeletedAtUtc==null&&ownerSources.Contains(x.Data.SourceEventId)).Select(x=>new Target(x.Data.ChatId,x.Data.MessageId,x.Row.Id)).ToList();
        if(invoice.TelegramInvoiceMessageId is >0&&!string.IsNullOrWhiteSpace(invoice.TelegramInvoiceChatId))cleanup.Add(new(invoice.TelegramInvoiceChatId,invoice.TelegramInvoiceMessageId.Value,null));
        foreach(var n in events.Where(n=>n.CustomerId==order.CustomerId&&(n.EventType.StartsWith("Invoice",StringComparison.Ordinal)||n.EventType is "TelegramCustomerGroupRequired" or "TelegramGroupDeliveryFailed")))
        {n.IsDeleted=true;n.Status=NotificationOutboxStatus.Processed;n.ProcessedAt=now;n.LockedUntil=null;n.NextAttemptAt=null;n.LastError="Superseded by selected-item cancellation";n.UpdatedAt=now;}
        foreach(var artifact in await db.OrderArtifacts.Where(a=>a.OrderId==order.Id&&!a.IsDeleted&&a.Type==OrderArtifactType.InvoicePdf&&ownerSources.Contains(a.SourceEventId)).ToArrayAsync(ct))
        {artifact.IsDeleted=true;artifact.UpdatedAt=now;}
        foreach(var payment in order.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Pending))
        {payment.Status=PaymentStatus.Cancelled;payment.IsSuccessful=false;payment.UpdatedAt=now;}

        var cancelledOrder=new Order{Id=Guid.NewGuid(),CustomerId=order.CustomerId,Customer=order.Customer,
            DeliveryAddressId=order.DeliveryAddressId,OrderNumber="CNL-"+operation.ToString("N"),
            Source=order.Source,IsInventory=order.IsInventory,InvoiceIssuanceBatchId=order.InvoiceIssuanceBatchId,
            SalesListId=removed.Select(x=>x.SalesListId).Distinct().Count()==1?removed[0].SalesListId:null,Status=order.Status,InvoiceIssuedAt=order.InvoiceIssuedAt,
            RegisteredAt=order.RegisteredAt,Notes=$"Selected-item cancellation from invoice {invoice.InvoiceNumber} ({invoice.Id:N}); operation {operation:N}",CreatedAt=now};
        foreach(var item in removed){order.Items.Remove(item);cancelledOrder.Items.Add(item);item.Order=cancelledOrder;item.OrderId=cancelledOrder.Id;}
        cancelledOrder.PerfumeTotal=removed.Sum(x=>x.PerfumeAmount);cancelledOrder.BottleTotal=removed.Sum(x=>x.BottlePrice);cancelledOrder.FinalAmount=removed.Sum(x=>x.LineTotal);
        var cancelled=new Invoice{Id=Guid.NewGuid(),Order=cancelledOrder,OrderId=cancelledOrder.Id,
            InvoiceNumber=invoice.InvoiceNumber[..Math.Min(32,invoice.InvoiceNumber.Length)]+"-C"+operation.ToString("N")[..12],
            Status=invoice.Status,IssuedAt=invoice.IssuedAt,PerfumeTotal=cancelledOrder.PerfumeTotal,BottleTotal=cancelledOrder.BottleTotal,TotalAmount=cancelledOrder.FinalAmount,CreatedAt=now};
        db.Orders.Add(cancelledOrder);db.Invoices.Add(cancelled);
        order.PerfumeTotal=retained.Sum(x=>x.PerfumeAmount);order.BottleTotal=retained.Sum(x=>x.BottlePrice);order.FinalAmount=retained.Sum(x=>x.LineTotal);order.UpdatedAt=now;
        if(!retained.Any(x=>x.SalesListId==order.SalesListId))order.SalesListId=retained[0].SalesListId;
        invoice.PerfumeTotal=order.PerfumeTotal;invoice.BottleTotal=order.BottleTotal;invoice.TotalAmount=order.FinalAmount;invoice.UpdatedAt=now;
        invoice.TelegramInvoiceChatId=null;invoice.TelegramInvoiceMessageId=null;invoice.IsSentToCustomer=false;invoice.SentToCustomerAt=null;
        invoice.DeliveryStatus=InvoiceDeliveryStatus.RetryScheduled;invoice.DeliveryStatusChangedAt=now;invoice.DeliveryStatusNote="ارسال فاکتور اصلاح‌شده پس از حذف آیتم در صف است.";
        var groups=await db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==order.CustomerId).Take(2).ToArrayAsync(ct);
        if(groups.Length>1)throw new InvalidOperationException("گروه مقصد مشتری یکتا نیست؛ ابتدا اتصال را بررسی کنید.");
        var group=groups.SingleOrDefault();
        if(group is not null&&await db.CustomerTelegramGroups.AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==group.ChatId&&g.CustomerId!=order.CustomerId,ct))throw new InvalidOperationException("گروه مشتری به حساب دیگری هم متصل است.");
        var customer=order.Customer!;
        var accounts=await db.InvoicePaymentAccounts.AsNoTracking().Where(a=>!a.IsDeleted&&a.IsActive).OrderBy(a=>a.DisplayOrder).Take(4).ToArrayAsync(ct);
        var payload=new{OrderId=order.Id,order.OrderNumber,InvoiceId=invoice.Id,invoice.InvoiceNumber,invoice.IssuedAt,invoice.PerfumeTotal,invoice.BottleTotal,invoice.TotalAmount,
            Customer=new{customer.Id,customer.FullName,customer.Mobile,customer.TelegramId,customer.Username},
            order.IsInventory,InvoiceRevisionOperationId=operation,PaymentDeadlineHours=invoice.TotalAmount>0?24:0,
            PaymentAccounts=accounts.Select(a=>new{a.CardNumber,a.AccountHolder,a.BankName}),
            Items=retained.OrderBy(x=>x.RowNumber).ThenBy(x=>x.Id).Select(x=>new{x.RowNumber,
                PerfumePersianName=Name(x),PerfumeEnglishName=x.SalesList?.EnglishName??x.Perfume?.EnglishName,PerfumeBrand=x.Perfume?.Brand,
                x.RequestedVolumeMl,x.PerfumePricePerMl,x.PerfumeAmount,x.IsBottleOwner,IsGift=x.SourceSalesListRequest?.IsGift==true,
                GiftRecipientUsername=x.SourceSalesListRequest?.GiftRecipientTelegramUsername,GiftRecipientTelegramId=x.SourceSalesListRequest?.GiftRecipientTelegramUserId,
                BottleName=x.Bottle?.Name,x.BottlePrice,x.LineTotal})};
        var delivery=group is null?N8nIntegrationEventFactory.Create(order,"InvoiceIssued",payload,now):
            N8nIntegrationEventFactory.CreateForDelivery(order,customer,group.ChatId,payload,now);
        if(oldOwnerPayload is not null)
        {
            var old=JsonNode.Parse(oldOwnerPayload)!;var revised=JsonNode.Parse(delivery.Payload)!;
            foreach(var key in new[]{"GiftDeliveryRole","GiftRecipientUsername","GiftRecipientTelegramId"})if(old[key] is not null)revised[key]=old[key]!.DeepClone();
            delivery.Payload=revised.ToJsonString();
        }
        db.NotificationOutbox.Add(delivery);
        var telegramEvents=new List<Guid>();
        if(group is not null)
        {
            void Telegram(string kind,object body)
            {
                var id=Guid.NewGuid();telegramEvents.Add(id);db.NotificationOutbox.Add(new(){Id=id,CustomerId=order.CustomerId,OrderId=order.Id,
                    Channel="Telegram",EventType=kind,Recipient=group.ChatId,Payload=JsonSerializer.Serialize(body),CreatedAt=now.AddTicks(telegramEvents.Count)});
            }
            Telegram("InvoiceGreeting",new{});
            foreach(var file in retained.Where(x=>x.SourceSalesListRequest?.IsGift!=true&&!string.IsNullOrWhiteSpace(x.SalesList?.TelegramPhotoFileId))
                .Select(x=>x.SalesList!.TelegramPhotoFileId!).Distinct())Telegram("InvoicePerfumePhoto",new{FileId=file});
        }
        // Old messages remain until the exact replacement PDF has been delivered.
        db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=order.CustomerId,OrderId=order.Id,Channel=Channel,
            EventType="CompleteSelectedItemCancellation",Recipient=operation.ToString(),CreatedAt=now,
            Payload=JsonSerializer.Serialize(new Progress(operation,invoice.Id,order.Id,order.CustomerId,delivery.Id,telegramEvents.ToArray(),cleanup.DistinctBy(t=>(t.Chat,t.Message)).ToArray(),now))});
        return cancelled;
    }
    public static string Name(OrderItem item)=>new[]{item.SalesList?.PersianName,item.Perfume?.Name,item.ManualDescription,item.SalesList?.EnglishName}
        .FirstOrDefault(s=>!string.IsNullOrWhiteSpace(s))??"عطر";

    private static void UpdateGiftNotifications(AppDbContext db,Order order,OrderItem[] removed,OrderItem[] retained,NotificationOutbox[] events,DateTime now)
    {
        var all=removed.Concat(retained).ToArray();
        void Supersede(NotificationOutbox n)
        {n.IsDeleted=true;n.Status=NotificationOutboxStatus.Processed;n.ProcessedAt=now;n.LockedUntil=null;n.NextAttemptAt=null;n.LastError="Gift items revised after selected-item cancellation";n.UpdatedAt=now;}
        bool IsRemoved(JsonNode item)
        {
            var name=InvoiceCaptionIdentity.Normalize(item["PerfumePersianName"]?.GetValue<string>()??item["PerfumeEnglishName"]?.GetValue<string>()??"");
            var volume=item["RequestedVolumeMl"]?.GetValue<decimal>()??0;
            var matches=all.Where(x=>x.RequestedVolumeMl==volume&&new[]{Name(x),x.SalesList?.EnglishName,x.Perfume?.EnglishName}.Any(s=>s is not null&&InvoiceCaptionIdentity.Normalize(s)==name)).ToArray();
            if(matches.Length>1&&item["RowNumber"]?.GetValue<int>() is >0 and var row)
            {var numbered=matches.Where(x=>x.RowNumber==row).ToArray();if(numbered.Length>0)matches=numbered;}
            if(matches.Length==0)throw new InvalidOperationException("رسید هدیه با آیتم‌های فاکتور تطبیق ندارد؛ بررسی حسابداری لازم است.");
            var count=matches.Count(x=>removed.Any(r=>r.Id==x.Id));
            if(count>0&&count<matches.Length)throw new InvalidOperationException("چند آیتم هدیهٔ کاملاً مشابه وجود دارد؛ تطبیق رسیدشان برای حذف جزئی یکتا نیست.");
            return count==matches.Length;
        }
        var changed=new List<(Guid Customer,JsonNode Original,JsonArray Kept)>();
        foreach(var source in events.Where(n=>n.CustomerId!=order.CustomerId&&n.Channel=="N8n"&&n.EventType=="InvoiceIssued"))
        {
            var body=JsonNode.Parse(source.Payload)!;
            if(body["GiftDeliveryRole"]?.GetValue<string>()!="Recipient"||body["Items"] is not JsonArray items)continue;
            var kept=new JsonArray(items.Where(i=>i is not null&&!IsRemoved(i)).Select(i=>i!.DeepClone()).ToArray());
            if(kept.Count==items.Count)continue;
            changed.Add((source.CustomerId,body.DeepClone(),kept));Supersede(source);
            if(kept.Count==0)continue;
            body["Items"]=kept.DeepClone();
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=source.CustomerId,OrderId=source.OrderId,Channel=source.Channel,EventType=source.EventType,Recipient=source.Recipient,Payload=body.ToJsonString(),CreatedAt=now});
        }
        foreach(var source in events.Where(n=>n.CustomerId!=order.CustomerId&&n.Channel=="Telegram"&&n.EventType=="GiftInvoiceIssued"))
        {
            var body=JsonNode.Parse(source.Payload)!;var name=InvoiceCaptionIdentity.Normalize(body["PerfumePersianName"]?.GetValue<string>()??body["PerfumeEnglishName"]?.GetValue<string>()??"");
            var volume=body["RequestedVolumeMl"]?.GetValue<decimal>()??0;
            var match=changed.Where(c=>c.Customer==source.CustomerId&&c.Original["Items"] is JsonArray items&&
                items.Sum(i=>i!["RequestedVolumeMl"]!.GetValue<decimal>())==volume&&
                InvoiceCaptionIdentity.Normalize(string.Join("، ",items.Select(i=>i!["PerfumePersianName"]?.GetValue<string>()??i["PerfumeEnglishName"]?.GetValue<string>()??"")))==name).ToArray();
            if(match.Length!=1)continue;
            Supersede(source);if(match[0].Kept.Count==0)continue;
            body["PerfumePersianName"]=string.Join("، ",match[0].Kept.Select(i=>i!["PerfumePersianName"]?.GetValue<string>()??i["PerfumeEnglishName"]?.GetValue<string>()??""));
            body["RequestedVolumeMl"]=match[0].Kept.Sum(i=>i!["RequestedVolumeMl"]!.GetValue<decimal>());
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=source.CustomerId,OrderId=source.OrderId,Channel=source.Channel,EventType=source.EventType,Recipient=source.Recipient,Payload=body.ToJsonString(),CreatedAt=now});
        }
        foreach(var source in events.Where(n=>n.CustomerId!=order.CustomerId&&n.Channel=="Telegram"&&n.EventType=="InvoicePerfumePhoto"))
        {
            var body=JsonNode.Parse(source.Payload)!;
            if(body["SalesListId"]?.GetValue<Guid>() is Guid list&&removed.Any(x=>x.SalesListId==list)&&!retained.Any(x=>x.SalesListId==list))Supersede(source);
        }
    }
}
