using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class InvoiceTransferService(AppDbContext db)
{
    public const string ReceiptChannel="InvoiceTransferReceipt",WorkChannel="InvoiceTransferFollowup";
    public sealed record Selection(Guid InvoiceId,Guid FromId,string Number);
    public sealed record Preview(Selection Source,Guid ToId,string From,string To,string ChatId,Guid GroupId,decimal Amount,decimal Paid,decimal Outstanding,string Status,string[] Lines,string Fingerprint);
    public sealed record MessageTarget(string Chat,long Message,Guid? ReceiptId);
    public sealed record Result(Guid InvoiceId,Guid OrderId,string Number,Guid FromId,Guid ToId,string From,string To,string ToChat,
        decimal Amount,decimal Paid,decimal DebtMoved,long Actor,Guid DeliveryEvent,Guid[] TelegramEvents,MessageTarget[] Cleanup,DateTime CreatedAt,bool NeedsManualSourceCleanup=false);
    public sealed record Progress(Guid Operation,bool ReportUpdated=false,int Deleted=0,bool Complete=false,string? Error=null,bool ReportMissing=false);
    private IQueryable<Invoice> Query()=>db.Invoices.Include(i=>i.Order).ThenInclude(o=>o!.Customer)
        .Include(i=>i.Order).ThenInclude(o=>o!.Payments).Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.SalesList)
        .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.Perfume).Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.Bottle)
        .Include(i=>i.Order).ThenInclude(o=>o!.Items).ThenInclude(x=>x.SourceSalesListRequest);
    public static string Label(Customer c)=>string.IsNullOrWhiteSpace(c.Username)?c.TelegramId??c.FullName:"@"+c.Username.TrimStart('@');
    public async Task<Selection> FindAsync(string caption,CancellationToken ct=default)
    {
        var id=await new InvoiceCancellationService(db).ResolveIdentityAsync(caption,ct);
        var invoice=await Query().AsNoTracking().SingleAsync(i=>i.Id==id,ct);await ValidateAsync(invoice,ct);
        return new(id,invoice.Order!.CustomerId,invoice.InvoiceNumber);
    }
    private async Task ValidateAsync(Invoice i,CancellationToken ct)
    {
        var o=i.Order??throw new InvalidOperationException("سفارش پیدا نشد.");
        if(i.IsDeleted||o.IsDeleted||o.Customer is null||o.Customer.IsDeleted||i.Status is InvoiceStatus.Cancelled or InvoiceStatus.Draft||o.Status==OrderStatus.Cancelled)
            throw new InvalidOperationException("این فاکتور فعال نیست.");
        var items=o.Items.Where(x=>!x.IsDeleted).ToArray();
        if(items.Length==0||i.TotalAmount<=0||i.TotalAmount!=o.FinalAmount||items.Sum(x=>x.LineTotal)!=i.TotalAmount)
            throw new InvalidOperationException("مبلغ فاکتور و آیتم‌ها سازگار نیست؛ ابتدا حسابداری بررسی کند.");
        if(o.Status is OrderStatus.Shipped or OrderStatus.Delivered||o.ShippedAt!=null||items.Any(x=>x.ShippedAt!=null||x.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped))
            throw new InvalidOperationException("فاکتور دارای آیتم ارسال‌شده قابل انتقال نیست.");
        if(items.Any(x=>x.ShippingRequestId!=null))throw new InvalidOperationException("این فاکتور در بستهٔ آماده‌سازی قرار دارد؛ ابتدا اقلامش را از درخواست ارسال قبلی خارج کنید.");
        if(items.Any(x=>x.SourceSalesListRequest?.IsGift==true)||o.Notes?.Contains(ShippingRecipientPolicy.ManualGiftMarker)==true||
            await db.NotificationOutbox.AnyAsync(n=>!n.IsDeleted&&n.OrderId==o.Id&&n.Channel=="Telegram"&&(n.EventType=="GiftInvoiceIssued"||n.EventType=="InvoiceGiftDeliveryRequiresManualAction"),ct))
            throw new InvalidOperationException("این فاکتور شامل هدیه با گیرندهٔ مستقل است؛ برای جلوگیری از تغییر مالکیت هدیه، انتقال یکجای آن مجاز نیست.");
        if(o.Payments.Any(p=>!p.IsDeleted&&p.Status is PaymentStatus.Pending or PaymentStatus.Refunded))
            throw new InvalidOperationException("فیش در انتظار بررسی یا بازپرداخت وجود دارد؛ ابتدا حسابداری تعیین تکلیف کند.");
        var confirmed=o.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed).Sum(p=>p.Amount);
        if(confirmed>i.TotalAmount||o.Status==OrderStatus.Paid&&i.Status!=InvoiceStatus.Paid||i.Status!=InvoiceStatus.Paid&&confirmed==i.TotalAmount)
            throw new InvalidOperationException("وضعیت پرداخت سازگار نیست؛ ابتدا تأیید پرداخت فاکتور را در حسابداری اصلاح کنید.");
        if(await db.Invoices.AnyAsync(v=>v.OrderId==o.Id&&v.Id!=i.Id&&!v.IsDeleted&&v.Status!=InvoiceStatus.Cancelled&&v.Status!=InvoiceStatus.Draft,ct))
            throw new InvalidOperationException("این سفارش چند فاکتور فعال دارد؛ بررسی حسابداری لازم است.");
        if(await db.NotificationOutbox.AnyAsync(n=>!n.IsDeleted&&n.OrderId==o.Id&&n.Channel==WorkChannel&&n.Status!=NotificationOutboxStatus.Processed,ct))
            throw new InvalidOperationException("انتقال قبلی این فاکتور هنوز در حال تکمیل است؛ وضعیت آن را بررسی کنید.");
        var requestIds=items.Where(x=>x.SourceSalesListRequestId!=null).Select(x=>x.SourceSalesListRequestId!.Value).ToArray();
        if(await db.OrderItems.AnyAsync(x=>!x.IsDeleted&&x.OrderId!=o.Id&&x.SourceSalesListRequestId!=null&&requestIds.Contains(x.SourceSalesListRequestId.Value),ct))
            throw new InvalidOperationException("درخواست یکی از آیتم‌ها به فاکتور دیگری هم متصل است؛ انتقال متوقف شد.");
    }
    private async Task<Customer> RecipientAsync(string identity,CancellationToken ct)
    {
        var value=InvoiceCaptionIdentity.Digits(identity.Trim());
        if(value.StartsWith("https://t.me/",StringComparison.OrdinalIgnoreCase))value=value[13..];
        var numeric=long.TryParse(value,out var id)&&id>0;var name=value.TrimStart('@').ToLowerInvariant();
        if(!numeric&&!Regex.IsMatch(name,"^[a-z0-9_]{5,32}$"))throw new InvalidOperationException("آیدی معتبر @username یا شناسهٔ عددی تلگرام وارد کنید.");
        var rows=await db.Customers.AsNoTracking().Where(c=>!c.IsDeleted&&(numeric?c.TelegramId==value:c.Username!=null&&c.Username.Replace("@","").ToLower()==name)).Take(2).ToArrayAsync(ct);
        if(rows.Length!=1)throw new InvalidOperationException("حساب یکتای گیرنده پیدا نشد؛ ابتدا مشتری و گروه حسابداری او را متصل کنید.");
        return rows[0];
    }
    private async Task<CustomerTelegramGroup> GroupAsync(Guid customer,CancellationToken ct)
    {
        var rows=await db.CustomerTelegramGroups.AsNoTracking().Where(g=>g.CustomerId==customer&&!g.IsDeleted&&g.IsActive).Take(2).ToArrayAsync(ct);
        if(rows.Length!=1||!long.TryParse(rows[0].ChatId,out var chat)||chat>=0)throw new InvalidOperationException("گیرنده باید دقیقاً یک گروه حسابداری فعال و متصل داشته باشد.");
        if(await db.CustomerTelegramGroups.AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==rows[0].ChatId&&g.CustomerId!=customer,ct))
            throw new InvalidOperationException("گروه گیرنده به بیش از یک مشتری متصل است؛ ابتدا اتصال اصلاح شود.");
        return rows[0];
    }
    public async Task<Preview> PreviewAsync(Selection source,string identity,CancellationToken ct=default)
    {
        var i=await Query().AsNoTracking().SingleAsync(i=>i.Id==source.InvoiceId,ct);await ValidateAsync(i,ct);
        if(i.Order!.CustomerId!=source.FromId)throw new InvalidOperationException("مالک فاکتور پس از ورود کپشن عوض شده؛ دوباره شروع کنید.");
        var to=await RecipientAsync(identity,ct);if(to.Id==source.FromId)throw new InvalidOperationException("گیرنده با مالک فعلی یکی است.");
        var group=await GroupAsync(to.Id,ct);
        if(await db.CustomerTelegramGroups.AnyAsync(g=>g.CustomerId==source.FromId&&g.ChatId==group.ChatId,ct))throw new InvalidOperationException("گروه مبدا و مقصد یکسان است؛ ابتدا اتصال گروه گیرنده اصلاح شود.");
        return await BuildAsync(i,to,group,ct);
    }
    private Task<Preview> BuildAsync(Invoice i,Customer to,CustomerTelegramGroup group,CancellationToken ct)
    {
        var o=i.Order!;var items=o.Items.Where(x=>!x.IsDeleted).OrderBy(x=>x.Id).ToArray();
        var paid=i.Status==InvoiceStatus.Paid?i.TotalAmount:o.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed).Sum(p=>p.Amount);
        var data=JsonSerializer.Serialize(new{i.Id,i.Status,i.TotalAmount,i.IssuedAt,i.TelegramInvoiceChatId,i.TelegramInvoiceMessageId,
            o.CustomerId,OrderStatus=o.Status,o.RowVersion,From=Label(o.Customer!),To=Label(to),ToId=to.Id,to.TelegramId,Group=group.Id,group.ChatId,
            Payments=o.Payments.Where(p=>!p.IsDeleted).OrderBy(p=>p.Id).Select(p=>new{p.Id,p.Status,p.Amount,p.RowVersion}),
            Items=items.Select(x=>new{x.Id,x.SalesListId,x.RequestedVolumeMl,x.LineTotal,x.FulfillmentStatus,x.ShippingRequestId,x.ShippedAt,x.SourceSalesListRequestId,RequestVersion=x.SourceSalesListRequest?.RowVersion})});
        return Task.FromResult(new Preview(new(i.Id,o.CustomerId,i.InvoiceNumber),to.Id,Label(o.Customer!),Label(to),group.ChatId,group.Id,i.TotalAmount,paid,i.TotalAmount-paid,
            i.Status==InvoiceStatus.Paid?"✅ پرداخت‌شده":paid>0?"⏳ بخشی پرداخت شده":"🔴 پرداخت‌نشده",
            items.Select(x=>$"{Name(x)} — {x.RequestedVolumeMl:0.#} میل — {x.LineTotal:N0} تومان").ToArray(),Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))));
    }
    private static string Name(OrderItem x)=>x.SalesList?.PersianName is {Length:>0}?x.SalesList.PersianName:x.Perfume?.Name??x.ManualDescription??x.SalesList?.EnglishName??"عطر";
    public async Task<Result> ExecuteAsync(Guid operation,Preview expected,long actor,CancellationToken ct=default)
    {
        if(operation==Guid.Empty||actor<=0||expected.ToId==expected.Source.FromId||db.ChangeTracker.HasChanges())throw new InvalidOperationException("درخواست انتقال معتبر نیست.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"invoice-cancel:"+expected.Source.InvoiceId},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Invoice accounting operation busy',1;",ct);
        var prior=await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==operation,ct);
        if(prior!=null)
        {
            var saved=prior.Channel==ReceiptChannel?JsonSerializer.Deserialize<Result>(prior.Payload):null;
            if(saved is null||saved.InvoiceId!=expected.Source.InvoiceId||saved.ToId!=expected.ToId||saved.Actor!=actor)throw new InvalidOperationException("شناسهٔ انتقال تکراری نامعتبر است.");
            await tx.CommitAsync(ct);return saved;
        }
        var initial=await db.Invoices.AsNoTracking().Where(i=>i.Id==expected.Source.InvoiceId).Select(i=>new{i.OrderId}).SingleAsync(ct);
        var listIds=await db.OrderItems.AsNoTracking().Where(x=>x.OrderId==initial.OrderId&&!x.IsDeleted&&x.SalesListId!=null).Select(x=>x.SalesListId!.Value).Distinct().ToArrayAsync(ct);
        foreach(var id in listIds.Order())await db.SalesLists.FromSqlInterpolated($"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        foreach(var id in new[]{expected.Source.FromId,expected.ToId}.Order())await db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        await db.Orders.FromSqlInterpolated($"SELECT * FROM Orders WITH(UPDLOCK,HOLDLOCK) WHERE Id={initial.OrderId}").AsNoTracking().SingleAsync(ct);
        await db.Invoices.FromSqlInterpolated($"SELECT * FROM Invoices WITH(UPDLOCK,HOLDLOCK) WHERE Id={expected.Source.InvoiceId}").AsNoTracking().SingleAsync(ct);
        await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WITH(UPDLOCK,HOLDLOCK) WHERE OrderId={initial.OrderId} AND IsDeleted=0").AsNoTracking().ToArrayAsync(ct);
        await db.SalesListRequests.FromSqlInterpolated($"SELECT * FROM SalesListRequests WITH(UPDLOCK,HOLDLOCK) WHERE Id IN(SELECT SourceSalesListRequestId FROM OrderItems WHERE OrderId={initial.OrderId} AND IsDeleted=0)").AsNoTracking().ToArrayAsync(ct);
        await db.CustomerTelegramGroups.FromSqlInterpolated($"SELECT * FROM CustomerTelegramGroups WITH(UPDLOCK,HOLDLOCK) WHERE CustomerId={expected.ToId} OR ChatId={expected.ChatId}").AsNoTracking().ToArrayAsync(ct);
        db.ChangeTracker.Clear();var invoice=await Query().SingleAsync(i=>i.Id==expected.Source.InvoiceId,ct);await ValidateAsync(invoice,ct);
        var to=await db.Customers.SingleAsync(c=>c.Id==expected.ToId&&!c.IsDeleted,ct);var group=await GroupAsync(to.Id,ct);
        if(await db.CustomerTelegramGroups.AnyAsync(g=>g.CustomerId==expected.Source.FromId&&g.ChatId==group.ChatId,ct))
            throw new InvalidOperationException("گروه مبدا و مقصد یکسان شده؛ اتصال گیرنده را دوباره بررسی کنید.");
        var fresh=await BuildAsync(invoice,to,group,ct);
        if(fresh.Fingerprint!=expected.Fingerprint)throw new InvalidOperationException("فاکتور، گیرنده، پرداخت یا اقلام بعد از پیش‌نمایش تغییر کرده؛ دوباره بررسی کنید.");
        var order=invoice.Order!;var from=order.Customer!;
        if(from.CurrentDebt<fresh.Outstanding)throw new InvalidOperationException("بدهی مبدا با ماندهٔ فاکتور سازگار نیست؛ حسابداری باید بررسی کند.");
        var events=await db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE OrderId={order.Id} AND IsDeleted=0 AND Channel IN(N'Telegram',N'N8n')").ToArrayAsync(ct);
        var now=DateTime.UtcNow;
        if(events.Any(n=>n.Status==NotificationOutboxStatus.Processing&&n.LockedUntil>now))throw new InvalidOperationException("ارسال پیام‌های این فاکتور در حال انجام است؛ کمی بعد دوباره تلاش کنید.");
        var messages=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==order.Id&&n.Channel=="InvoiceMessageReceipt"&&!n.IsDeleted).ToArrayAsync(ct);
        var cleanup=messages.Select(n=>(Row:n,Value:JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(n.Payload)!))
            .Where(x=>x.Value.InvoiceId==invoice.Id&&x.Value.DeletedAtUtc==null).Select(x=>new MessageTarget(x.Value.ChatId,x.Value.MessageId,x.Row.Id)).ToList();
        if(invoice.TelegramInvoiceMessageId is >0&&!string.IsNullOrWhiteSpace(invoice.TelegramInvoiceChatId)&&!cleanup.Any(x=>x.Chat==invoice.TelegramInvoiceChatId&&x.Message==invoice.TelegramInvoiceMessageId))
            cleanup.Add(new(invoice.TelegramInvoiceChatId,invoice.TelegramInvoiceMessageId.Value,null));
        var knownSources=messages.Select(n=>JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(n.Payload)!.SourceEventId).ToHashSet();
        var untracked=invoice.IsSentToCustomer&&(cleanup.Count==0||events.Any(n=>n.Status==NotificationOutboxStatus.Processed&&
            (n.EventType is "InvoiceGreeting" or "InvoicePerfumePhoto"||n.Channel=="N8n"&&n.EventType=="InvoiceIssued"&&invoice.TelegramInvoiceMessageId is null)&&!knownSources.Contains(n.Id)));
        foreach(var n in events)
        {
            if(n.Status!=NotificationOutboxStatus.Processed){n.Status=NotificationOutboxStatus.Processed;n.ProcessedAt=now;n.LockedUntil=null;n.NextAttemptAt=null;n.LastError="Superseded by invoice transfer";}
            if(n.EventType.StartsWith("Invoice",StringComparison.Ordinal)||n.EventType is "TelegramCustomerGroupRequired" or "TelegramGroupDeliveryFailed")n.IsDeleted=true;
        }
        foreach(var artifact in await db.OrderArtifacts.Where(a=>a.OrderId==order.Id&&!a.IsDeleted&&a.Type==OrderArtifactType.InvoicePdf).ToArrayAsync(ct)){artifact.IsDeleted=true;artifact.UpdatedAt=now;}
        from.CurrentDebt-=fresh.Outstanding;to.CurrentDebt+=fresh.Outstanding;from.UpdatedAt=now;to.UpdatedAt=now;
        order.CustomerId=to.Id;order.Customer=to;order.DeliveryAddressId=null;order.UpdatedAt=now;
        var telegramId=long.TryParse(to.TelegramId,out var userId)&&userId>0?userId.ToString():"admin-username:"+to.Username?.TrimStart('@').ToLowerInvariant();
        foreach(var item in order.Items.Where(x=>!x.IsDeleted))
        {
            if(item.SourceSalesListRequest is {} request)
            {request.TelegramUserId=telegramId;request.TelegramUsername=to.Username?.TrimStart('@');request.LabelIdentityText=null;request.OmitIdentityOnLabel=false;request.UpdatedAt=now;}
            item.ShippingRequestedAt=null;item.ShippingTelegramMessageId=null;item.UpdatedAt=now;
        }
        invoice.TelegramInvoiceChatId=null;invoice.TelegramInvoiceMessageId=null;invoice.IsSentToCustomer=false;invoice.SentToCustomerAt=null;
        invoice.DeliveryStatus=InvoiceDeliveryStatus.RetryScheduled;invoice.DeliveryStatusChangedAt=now;invoice.DeliveryStatusNote="ارسال نسخهٔ منتقل‌شده به گروه گیرنده در صف است.";invoice.UpdatedAt=now;
        var notificationIds=new List<Guid>();
        void Telegram(string type,object payload)
        {
            var id=Guid.NewGuid();notificationIds.Add(id);db.NotificationOutbox.Add(new(){Id=id,CustomerId=to.Id,OrderId=order.Id,Channel="Telegram",EventType=type,Recipient=group.ChatId,
                Payload=JsonSerializer.Serialize(payload),CreatedAt=now.AddTicks(notificationIds.Count)});
        }
        Telegram("InvoiceGreeting",new{});
        var productPhotos=order.Items.Where(x=>!x.IsDeleted&&!string.IsNullOrWhiteSpace(x.SalesList?.TelegramPhotoFileId)).Select(x=>x.SalesList!.TelegramPhotoFileId!)
            .Concat(events.Where(n=>n.EventType=="InvoicePerfumePhoto").Select(n=>{try{using var d=JsonDocument.Parse(n.Payload);return d.RootElement.TryGetProperty("FileId",out var f)?f.GetString():null;}catch(JsonException){return null;}}).OfType<string>()).Distinct();
        foreach(var file in productPhotos)Telegram("InvoicePerfumePhoto",new{FileId=file});
        var accounts=await db.InvoicePaymentAccounts.AsNoTracking().Where(a=>!a.IsDeleted&&a.IsActive).OrderBy(a=>a.DisplayOrder).Take(4).ToArrayAsync(ct);
        var delivery=Guid.NewGuid();
        var payload=new{OrderId=order.Id,order.OrderNumber,InvoiceId=invoice.Id,invoice.InvoiceNumber,invoice.IssuedAt,invoice.PerfumeTotal,invoice.BottleTotal,invoice.TotalAmount,
            Customer=new{to.Id,to.FullName,to.Mobile,to.TelegramId,to.Username},order.IsInventory,IsTransferredInvoice=true,TransferOperationId=operation,IsPaid=invoice.Status==InvoiceStatus.Paid,
            PaidAmount=fresh.Paid,OutstandingAmount=fresh.Outstanding,PaymentDeadlineHours=fresh.Outstanding>0?24:0,
            Delivery=new{Channel="TelegramGroup",group.ChatId,group.Title,group.Username},
            PaymentAccounts=accounts.Select(a=>new{a.CardNumber,a.AccountHolder,a.BankName}),Items=order.Items.Where(x=>!x.IsDeleted).OrderBy(x=>x.RowNumber).Select(x=>new
            {x.RowNumber,PerfumePersianName=Name(x),PerfumeEnglishName=x.SalesList?.EnglishName??x.Perfume?.EnglishName,PerfumeBrand=x.Perfume?.Brand,x.RequestedVolumeMl,x.PerfumePricePerMl,x.PerfumeAmount,x.IsBottleOwner,IsGift=false,BottleName=x.Bottle?.Name,x.BottlePrice,x.LineTotal})};
        db.NotificationOutbox.Add(new(){Id=delivery,CustomerId=to.Id,OrderId=order.Id,Channel="N8n",EventType="InvoiceIssued",Recipient="n8n",Payload=JsonSerializer.Serialize(payload),CreatedAt=now});
        var result=new Result(invoice.Id,order.Id,invoice.InvoiceNumber,from.Id,to.Id,fresh.From,fresh.To,group.ChatId,fresh.Amount,fresh.Paid,fresh.Outstanding,actor,delivery,notificationIds.ToArray(),cleanup.DistinctBy(x=>(x.Chat,x.Message)).ToArray(),now,untracked);
        db.NotificationOutbox.Add(new(){Id=operation,CustomerId=to.Id,OrderId=order.Id,Channel=ReceiptChannel,EventType="InvoiceTransferred",Recipient=actor.ToString(),Payload=JsonSerializer.Serialize(result),Status=NotificationOutboxStatus.Processed,CreatedAt=now,ProcessedAt=now});
        db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=to.Id,OrderId=order.Id,Channel=WorkChannel,EventType="FinishInvoiceTransfer",Recipient=actor.ToString(),Payload=JsonSerializer.Serialize(new Progress(operation)),CreatedAt=now});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
    }
}
