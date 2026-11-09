using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class InvoiceTransferTests
{
    private const long Owner=771234567;private const string PaymentChat="-1007755411";
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="xfer_"+Guid.NewGuid().ToString("N")[..15];var stamp=Random.Shared.Next(1000000,9000000).ToString();
        var from=new Customer{Id=Guid.NewGuid(),Username=marker+"a",FullName=marker+" A",TelegramId="991"+stamp,Mobile="091"+stamp+"1",CreatedAt=DateTime.UtcNow};
        var to=new Customer{Id=Guid.NewGuid(),Username=marker+"b",FullName=marker+" B",TelegramId="992"+stamp,Mobile="091"+stamp+"2",CreatedAt=DateTime.UtcNow};
        var fromChat="-10091"+stamp;var toChat="-10092"+stamp;
        var groupFrom=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=from.Id,ChatId=fromChat,Title=marker+"from",IsActive=true,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        var groupTo=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=to.Id,ChatId=toChat,Title=marker+"to",IsActive=true,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        var bottle=new Bottle{Id=Guid.NewGuid(),Name=marker,VolumeMl=5,Type=BottleType.Fancy,SalePrice=500,IsActive=true,CreatedAt=DateTime.UtcNow};
        db.Customers.AddRange(from,to);db.CustomerTelegramGroups.AddRange(groupFrom,groupTo);db.Bottles.Add(bottle);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var orders=new List<Guid>();var lists=new List<Guid>();var batches=new List<Guid>();var serial=8100;var service=new InvoiceTransferService(db);
        var sender=DispatchProxy.Create<ITelegramMessageSender,InvoiceTransferSender>();var sent=(InvoiceTransferSender)(object)sender;
        var invoiceService=new InvoiceIssuanceService(db,DispatchProxy.Create<ISender,ReadyBillingSender>());
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)!=null)field.SetValue(controller,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_invoiceIssuanceService",invoiceService),
            ("_options",new TelegramOptions{OwnerUserId=Owner.ToString(),NewPaymentsChatId=PaymentChat}),("_adminActivityContext",new ZibasheERP.Application.Services.AdminActivityContext()),("_logger",NullLogger<TelegramWebhookController>.Instance)})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task<object?> Call(string method,params object?[] args){var task=(Task)typeof(TelegramWebhookController).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        async Task<bool> Reject(Func<Task> operation){try{await operation();return false;}catch(InvalidOperationException){db.ChangeTracker.Clear();return true;}}
        async Task<(decimal From,decimal To)> Debts(){var rows=await db.Customers.AsNoTracking().Where(c=>c.Id==from.Id||c.Id==to.Id).ToArrayAsync();return(rows.Single(c=>c.Id==from.Id).CurrentDebt,rows.Single(c=>c.Id==to.Id).CurrentDebt);}
        async Task<Invoice> Create(bool paid=false,decimal partial=0)
        {
            db.ChangeTracker.Clear();var now=DateTime.UtcNow;var list=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(40000000,90000000),PersianName="عطر انتقال تست",EnglishName="Transfer perfume",PerfumeId=await db.Perfumes.Select(p=>p.Id).FirstAsync(),TotalVolume=3,ReservedVolume=3,PricePerMl=10000,Status=SalesListStatus.Invoiced,TelegramPhotoFileId="fixture-product",CreatedAt=now,OpenDate=now};
            lists.Add(list.Id);db.SalesLists.Add(list);var batch=new InvoiceIssuanceBatch{Id=Guid.NewGuid(),CreatedAt=now,CreatedByTelegramUserId=Owner.ToString()};batches.Add(batch.Id);db.InvoiceIssuanceBatches.Add(batch);
            db.InvoiceIssuanceBatchSalesLists.Add(new(){InvoiceIssuanceBatchId=batch.Id,SalesListId=list.Id,TelegramPaymentTrackingChatId=PaymentChat,TelegramPaymentTrackingMessageId=90000+serial});
            var request=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=from.TelegramId!,TelegramUsername=from.Username,VolumeMl=3,PerfumePricePerMl=10000,BottleId=bottle.Id,BottlePrice=500,Status=SalesListRequestStatus.Invoiced,CreatedAt=now,ExternalReference=marker+Guid.NewGuid().ToString("N")};db.SalesListRequests.Add(request);
            var order=new Order{Id=Guid.NewGuid(),CustomerId=from.Id,OrderNumber=marker+Guid.NewGuid().ToString("N"),Status=paid?OrderStatus.Paid:OrderStatus.Invoiced,PerfumeTotal=30000,BottleTotal=500,FinalAmount=30500,InvoiceIssuedAt=now,InvoiceIssuanceBatchId=batch.Id,CreatedAt=now,PaidAt=paid?now:null};orders.Add(order.Id);
            order.Items.Add(new(){Id=Guid.NewGuid(),SalesListId=list.Id,PerfumeId=list.PerfumeId,SourceSalesListRequestId=request.Id,RequestedVolumeMl=3,PerfumePricePerMl=10000,PerfumeAmount=30000,BottleId=bottle.Id,BottlePrice=500,LineTotal=30500,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,CreatedAt=now});db.Orders.Add(order);
            var invoice=new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber="INV-261103-"+(++serial),TotalAmount=30500,PerfumeTotal=30000,BottleTotal=500,Status=paid?InvoiceStatus.Paid:InvoiceStatus.Issued,IssuedAt=now,CreatedAt=now,IsSentToCustomer=true,DeliveryStatus=InvoiceDeliveryStatus.Delivered,TelegramInvoiceChatId=fromChat,TelegramInvoiceMessageId=serial*10+4};db.Invoices.Add(invoice);
            var received=paid?30500:partial;if(received>0)db.Payments.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,Amount=received,Status=PaymentStatus.Confirmed,IsSuccessful=true,TransactionId=marker+serial,PaymentMethod="fixture",PaidAt=now,CreatedAt=now});
            var payer=await db.Customers.SingleAsync(c=>c.Id==from.Id);payer.CurrentDebt+=30500-received;
            var eventIds=Enumerable.Range(0,4).Select(_=>Guid.NewGuid()).ToArray();
            for(var n=0;n<4;n++)db.NotificationOutbox.Add(new(){Id=eventIds[n],OrderId=order.Id,CustomerId=from.Id,Channel=n==3?"N8n":"Telegram",EventType=n==3?"InvoiceIssued":n==0?"InvoiceGreeting":n==1?"InvoicePerfumePhoto":"InvoiceIssued",Recipient=n==3?"n8n":fromChat,
                Payload=JsonSerializer.Serialize(new{InvoiceId=invoice.Id,Customer=new{from.Id,from.Username},Delivery=new{Channel="TelegramGroup",ChatId=fromChat},FileId="fixture-product"}),Status=NotificationOutboxStatus.Processed,CreatedAt=now});
            await db.SaveChangesAsync();
            for(var n=0;n<4;n++)await InvoiceMessageLifecycle.RecordAsync(db,order.Id,eventIds[n],fromChat,serial*10+n+1,n==3?"InvoicePdf":"InvoiceMessage",default);
            db.OrderArtifacts.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,SourceEventId=eventIds[3],Type=OrderArtifactType.InvoicePdf,FileUrl="https://fixture.invalid/old.pdf",ExternalFileId="old-owner-pdf",CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();return invoice;
        }
        string Caption(Invoice i,Customer? owner=null)=>$"🧾 فاکتور عطر @{(owner??from).Username}\nشماره فاکتور: {i.InvoiceNumber}\n🧴 عطر انتقال تست\nمقدار: 3 میل\nمبلغ قابل پرداخت: {i.TotalAmount} تومان";
        async Task<int?> Follow(InvoiceTransferService.Result result)
        {var job=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==result.OrderId&&n.Channel==InvoiceTransferService.WorkChannel);return(int?)await Call("ProcessInvoiceTransferFollowupAsync",job,CancellationToken.None);}
        async Task<InvoiceTransferService.Progress> Progress(InvoiceTransferService.Result result)=>JsonSerializer.Deserialize<InvoiceTransferService.Progress>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==result.OrderId&&n.Channel==InvoiceTransferService.WorkChannel)).Payload)!;
        async Task<IActionResult> Deliver(Guid source,string chat)
        {
            var api=new N8nIntegrationsController(null!,db,Options.Create(new TelegramOptions()),sender);
            var bytes=Encoding.UTF8.GetBytes("%PDF fixture");using var stream=new MemoryStream(bytes);var file=new FormFile(stream,0,bytes.Length,"document","invoice.pdf");
            return await api.SendTelegramInvoice(source,chat,"fixture caption","invoice.pdf",file,default);
        }
        try
        {
            foreach(var paid in new[]{false,true})
            {
                var invoice=await Create(paid);var oldOrder=await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==invoice.OrderId);var debt=await Debts();
                var selection=await service.FindAsync(Caption(invoice));var preview=await service.PreviewAsync(selection,"@"+to.Username);
                check(preview.Outstanding==(paid?0:30500)&&preview.Paid==(paid?30500:0),"transfer preview separates preserved "+(paid?"paid":"unpaid")+" invoice amounts from the debt to move");
                var op=Guid.NewGuid();var result=await service.ExecuteAsync(op,preview,Owner);var after=await Debts();
                var saved=await db.Invoices.AsNoTracking().Include(i=>i.Order).SingleAsync(i=>i.Id==invoice.Id);
                check(saved.InvoiceNumber==invoice.InvoiceNumber&&saved.Status==invoice.Status&&saved.TotalAmount==invoice.TotalAmount&&saved.Order!.CustomerId==to.Id&&after==(debt.From-preview.Outstanding,debt.To+preview.Outstanding),
                    (paid?"paid":"unpaid")+" invoice transfer preserves number, amount and payment status and moves only outstanding debt atomically");
                check(await db.SalesListRequests.AnyAsync(r=>r.TelegramUserId==to.TelegramId&&r.TelegramUsername==to.Username&&db.OrderItems.Any(x=>x.OrderId==invoice.OrderId&&x.SourceSalesListRequestId==r.Id)),"list item ownership and username move to the same stable recipient as the invoice");
                var replay=await service.ExecuteAsync(op,preview,Owner);check(replay.InvoiceId==result.InvoiceId&&replay.ToId==result.ToId&&replay.DebtMoved==result.DebtMoved&&await Debts()==after,"replayed transfer confirmation cannot move money or ownership twice");
                check(await Reject(async()=>{await new PaymentRepository(db).AddForOrderVersionAsync(new(){Id=Guid.NewGuid(),OrderId=invoice.OrderId,Amount=1,TransactionId=marker+"late"},from.Id,oldOrder.RowVersion);}),"receipt submission from the former owner's stale order snapshot is rejected after transfer");
                check(await db.OrderArtifacts.AsNoTracking().Where(a=>a.OrderId==invoice.OrderId&&a.Type==OrderArtifactType.InvoicePdf).AllAsync(a=>a.IsDeleted),"old PDF artifacts are retired so recipient resend cannot reuse a document bearing the former owner's name");
                var n8n=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==result.DeliveryEvent);using(var payload=JsonDocument.Parse(n8n.Payload))
                    check(payload.RootElement.GetProperty("Customer").GetProperty("Id").GetGuid()==to.Id&&payload.RootElement.GetProperty("IsPaid").GetBoolean()==paid&&payload.RootElement.GetProperty("Delivery").GetProperty("ChatId").GetString()==toChat,"new PDF payload has the recipient's identity/group and the preserved paid state");
                var beforeDelete=sent.Deleted.Count;check(await Follow(result)==5&&sent.Deleted.Count==beforeDelete,"source invoice messages are retained until actual PDF delivery to the recipient is proven");
                check(sent.Edits.Last().Text.Contains("@"+to.Username)&&!sent.Edits.Last().Text.Contains("@"+from.Username),"the original new-payments report replaces the giver's username with the receiver without posting a duplicate report");
                var oldSource=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==invoice.OrderId&&n.Channel=="N8n"&&n.Id!=result.DeliveryEvent).Select(n=>n.Id).FirstAsync();
                check(await Deliver(oldSource,fromChat)is ConflictObjectResult,"a delayed original-owner PDF upload cannot deliver after invoice transfer");
                var artifactApi=new N8nIntegrationsController(null!,db,Options.Create(new TelegramOptions()),sender);
                check(await artifactApi.RecordOrderArtifact(new(oldSource,invoice.OrderId,"InvoicePdf","https://fixture.invalid/late.pdf","old-late-pdf","application/pdf"),default)is ConflictObjectResult,
                    "an archived old-owner artifact callback cannot restore an old PDF as the receiver's resend document");
                var docCount=sent.Documents.Count;check(await Deliver(result.DeliveryEvent,toChat)is OkObjectResult&&sent.Documents.Count==docCount+1,"new PDF delivery uses the real controller and records the transfer generation's delivery proof");
                if(paid)check(sent.Documents.Last().Buttons.SelectMany(r=>r).All(b=>b.CopyText is null),"paid transferred invoice has no bank amount or card-copy button asking the new owner to pay again");
                await db.NotificationOutbox.Where(n=>result.TelegramEvents.Contains(n.Id)).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
                sent.FailDelete=result.Cleanup[1].Message;check(await Reject(async()=>{await Follow(result);})&&(await Progress(result)).Deleted==1,"partial source-bundle deletion persists progress for retry after new-owner delivery");
                sent.FailDelete=null;check(await Follow(result)is null&&(await Progress(result)).Complete&&result.Cleanup.All(t=>sent.Deleted.Contains((t.Chat,t.Message)))&&
                    !sent.Deleted.Any(t=>t.Chat==toChat),"successful cleanup removes only the old greeting/photo/caption/PDF bundle and preserves recipient messages");
                var done=await Debts();check(done==after,"delivery, report updates and cleanup retries do not repeat the financial transfer");
                await db.NotificationOutbox.Where(n=>n.OrderId==result.OrderId&&n.Channel==InvoiceTransferService.WorkChannel).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
                if(paid)
                {
                    var movedCaption=Caption(invoice,to).Replace("مبلغ قابل پرداخت","مبلغ کل فاکتور");
                    var back=await service.PreviewAsync(await service.FindAsync(movedCaption),"@"+from.Username);await service.ExecuteAsync(Guid.NewGuid(),back,Owner);
                    check(await Deliver(oldSource,fromChat)is ConflictObjectResult,"returning ownership to the first customer still cannot resurrect the first generation's archived PDF send");
                }
            }
            var partial=await Create(partial:10000);var partialDebts=await Debts();var pp=await service.PreviewAsync(await service.FindAsync(Caption(partial)),"@"+to.Username);
            var partialResult=await service.ExecuteAsync(Guid.NewGuid(),pp,Owner);var partialAfter=await Debts();
            check(pp.Paid==10000&&pp.Outstanding==20500&&partialAfter==(partialDebts.From-20500,partialDebts.To+20500)&&await db.Payments.AsNoTracking().AnyAsync(p=>p.OrderId==partial.OrderId&&p.Status==PaymentStatus.Confirmed&&p.Amount==10000),"partially paid transfer moves only the unpaid balance and preserves the original payment record");
            await db.NotificationOutbox.Where(n=>n.Id==partialResult.DeliveryEvent).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            await new InvoicePaymentStatusService(db).MarkPaidAsync(partial.Id,Owner);
            var paidForRetry=await db.Invoices.AsNoTracking().Include(i=>i.Order).SingleAsync(i=>i.Id==partial.Id);
            await Call("QueueInvoicePdfRegenerationAsync",paidForRetry,groupTo,CancellationToken.None);
            var rebuiltEvent=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==partial.OrderId&&n.Channel=="N8n"&&!n.IsDeleted&&n.Id!=partialResult.DeliveryEvent).SingleAsync();var rebuilt=rebuiltEvent.Payload;
            using(var rebuiltData=JsonDocument.Parse(rebuilt))check(rebuiltData.RootElement.GetProperty("IsPaid").GetBoolean()&&rebuiltData.RootElement.GetProperty("OutstandingAmount").GetDecimal()==0,
                "resending a transferred invoice after later full payment regenerates zero outstanding instead of reviving the transfer-time unpaid amount");
            await db.NotificationOutbox.Where(n=>partialResult.TelegramEvents.Contains(n.Id)).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            await Deliver(rebuiltEvent.Id,toChat);
            check(await Follow(partialResult)is null&&(await Progress(partialResult)).Complete,
                "a regenerated PDF for the same transfer operation provides valid recipient-delivery proof when the first generated copy was not delivered");
            var stale=await Create();var stalePreview=await service.PreviewAsync(await service.FindAsync(Caption(stale)),"@"+to.Username);await new InvoicePaymentStatusService(db).MarkPaidAsync(stale.Id,Owner);
            check(await Reject(async()=>{await service.ExecuteAsync(Guid.NewGuid(),stalePreview,Owner);})&&await db.Orders.AnyAsync(o=>o.Id==stale.OrderId&&o.CustomerId==from.Id),"payment confirmed after preview invalidates transfer instead of charging the recipient from an old unpaid snapshot");
            var pending=await Create();db.Payments.Add(new(){Id=Guid.NewGuid(),OrderId=pending.OrderId,Amount=1,Status=PaymentStatus.Pending,TransactionId=marker+"pending",CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            check(await Reject(async()=>{await service.FindAsync(Caption(pending));}),"a receipt still under accounting review prevents invoice transfer");
            var inFlight=await Create();var inFlightPreview=await service.PreviewAsync(await service.FindAsync(Caption(inFlight)),"@"+to.Username);
            var inFlightSource=await db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==inFlight.OrderId&&n.Channel=="N8n").Select(n=>n.Id).SingleAsync();
            await using(var deliveryDb=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options))
            await using(var transferDb=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options))
            {
                sent.DocumentStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);sent.DocumentRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
                var deliveryApi=new N8nIntegrationsController(null!,deliveryDb,Options.Create(new TelegramOptions()),sender);
                using var stream=new MemoryStream(Encoding.UTF8.GetBytes("%PDF in-flight"));var file=new FormFile(stream,0,stream.Length,"document","invoice.pdf");
                var delivery=deliveryApi.SendTelegramInvoice(inFlightSource,fromChat,"old invoice","invoice.pdf",file,default);
                var firstDeliveryStage=await Task.WhenAny(sent.DocumentStarted.Task,delivery);
                if(firstDeliveryStage==delivery)throw new InvalidOperationException("Fixture delivery ended before HTTP transport: "+(await delivery).GetType().Name);
                await sent.DocumentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var transferAttempt=new InvoiceTransferService(transferDb).ExecuteAsync(Guid.NewGuid(),inFlightPreview,Owner);
                await Task.WhenAny(transferAttempt,Task.Delay(100));var transferRacedDelivery=transferAttempt.IsCompleted;
                sent.DocumentRelease.TrySetResult(true);await delivery;
                check(!transferRacedDelivery,"invoice transfer waits for an already-started PDF delivery transaction rather than racing its ownership change");
                check(await Reject(async()=>{await transferAttempt;})&&await db.Orders.AsNoTracking().AnyAsync(o=>o.Id==inFlight.OrderId&&o.CustomerId==from.Id),
                    "completed old-owner PDF delivery invalidates the earlier transfer preview before any financial or ownership write");
                sent.DocumentStarted=null;sent.DocumentRelease=null;
            }
            var packed=await Create();await db.OrderItems.Where(x=>x.OrderId==packed.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ShippingRequestId,Guid.NewGuid()));
            check(await Reject(async()=>{await service.FindAsync(Caption(packed));}),"items already assigned to preparation cannot silently move between physical packages");
            var gift=await Create();await db.SalesListRequests.Where(r=>db.OrderItems.Any(x=>x.OrderId==gift.OrderId&&x.SourceSalesListRequestId==r.Id)).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.IsGift,true));
            check(await Reject(async()=>{await service.FindAsync(Caption(gift));}),"a gift with independent ownership cannot be reassigned by transferring its payer's invoice");
            var unlinked=await Create();var selectionUnlinked=await service.FindAsync(Caption(unlinked));await db.CustomerTelegramGroups.Where(g=>g.Id==groupTo.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,false));
            check(await Reject(async()=>{await service.PreviewAsync(selectionUnlinked,"@"+to.Username);}),"recipient without a connected accounting group is rejected before any ownership or balance changes");
            await db.CustomerTelegramGroups.Where(g=>g.Id==groupTo.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,true));
            foreach(var mode in new[]{"unpaid","paid","partial","manual"})
            {
                var multi=await Create(paid:mode=="paid",partial:mode=="partial"?10000:0);
                var first=await db.OrderItems.SingleAsync(x=>x.OrderId==multi.OrderId);
                first.FulfillmentStatus=OrderItemFulfillmentStatus.Shipped;first.ShippedAt=DateTime.UtcNow;
                var originalOrder=await db.Orders.SingleAsync(o=>o.Id==multi.OrderId);originalOrder.Status=OrderStatus.Shipped;originalOrder.ShippedAt=DateTime.UtcNow;
                var second=new OrderItem{Id=Guid.NewGuid(),OrderId=multi.OrderId,RowNumber=2,SalesListId=first.SalesListId,PerfumeId=first.PerfumeId,RequestedVolumeMl=2,
                    PerfumePricePerMl=10000,PerfumeAmount=20000,LineTotal=20000,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,CreatedAt=DateTime.UtcNow};db.OrderItems.Add(second);
                var selectedRequest=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=first.SalesListId!.Value,TelegramUserId=from.TelegramId!,TelegramUsername=from.Username,VolumeMl=2,PerfumePricePerMl=10000,Status=SalesListRequestStatus.Invoiced,CreatedAt=DateTime.UtcNow,ExternalReference=Guid.NewGuid().ToString("N")};db.SalesListRequests.Add(selectedRequest);second.SourceSalesListRequestId=selectedRequest.Id;
                originalOrder.FinalAmount+=20000;originalOrder.PerfumeTotal+=20000;
                var invoiceRow=await db.Invoices.SingleAsync(i=>i.Id==multi.Id);invoiceRow.TotalAmount+=20000;invoiceRow.PerfumeTotal+=20000;
                if(mode=="manual"){originalOrder.InvoiceIssuanceBatchId=null;originalOrder.Source=OrderSource.ManualInvoice;invoiceRow.TelegramPaymentTrackingChatId=PaymentChat;invoiceRow.TelegramPaymentTrackingMessageId=serial*100;}
                if(mode=="paid")db.Payments.Add(new(){Id=Guid.NewGuid(),OrderId=multi.OrderId,Amount=20000,Status=PaymentStatus.Confirmed,IsSuccessful=true,PaymentMethod="fixture",TransactionId=Guid.NewGuid().ToString("N"),CreatedAt=DateTime.UtcNow,PaidAt=DateTime.UtcNow});
                else(await db.Customers.SingleAsync(c=>c.Id==from.Id)).CurrentDebt+=20000;
                await db.SaveChangesAsync();db.ChangeTracker.Clear();var before=await Debts();
                var caption=$"🧾 فاکتور عطر @{from.Username}\nشماره فاکتور: {multi.InvoiceNumber}\nمبلغ قابل پرداخت: 50500 تومان";
                var choices=await service.FindItemsAsync(caption);
                check(choices.Lines.Length==2&&choices.Lines.Single(x=>x.Id==first.Id).Blocked=="ارسال‌شده"&&choices.Lines.Single(x=>x.Id==second.Id).Blocked is null,"mixed shipped invoice lists each item and blocks only the shipped row ("+mode+")");
                check(await Reject(async()=>{await service.SelectAsync(choices,[first.Id]);}),"a shipped line cannot be selected for partial transfer ("+mode+")");
                var selected=await service.SelectAsync(choices,[second.Id]);var preview=await service.PreviewAsync(selected,"@"+to.Username);
                var paid=mode=="paid"?20000:mode=="partial"?3960.40m:0;
                check(preview.Amount==20000&&preview.Paid==paid&&preview.Outstanding==20000-paid&&preview.Lines.Length==1,"partial preview includes only selected amount and its proportional paid allocation ("+mode+")");
                if(mode=="unpaid")
                {
                    await db.OrderItems.Where(x=>x.Id==second.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.ShippingRequestId,Guid.NewGuid()));
                    check(await Reject(async()=>{await service.ExecuteAsync(Guid.NewGuid(),preview,Owner);}),"assigning selected item to a parcel after preview prevents partial transfer without changing ownership");
                    await db.OrderItems.Where(x=>x.Id==second.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.ShippingRequestId,(Guid?)null));
                    selected=await service.SelectAsync(await service.FindItemsAsync(caption),[second.Id]);preview=await service.PreviewAsync(selected,"@"+to.Username);
                }
                var op=Guid.NewGuid();var moved=await service.ExecuteAsync(op,preview,Owner);orders.Add(moved.OrderId);db.ChangeTracker.Clear();
                var parent=await db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Items).SingleAsync(i=>i.Id==multi.Id);
                var child=await db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Items).SingleAsync(i=>i.Id==moved.InvoiceId);
                check(moved.SourceInvoiceId==multi.Id&&parent.InvoiceNumber==multi.InvoiceNumber&&parent.TotalAmount==30500&&parent.Order!.CustomerId==from.Id&&parent.Order.Items.Single().Id==first.Id&&parent.Order.Items.Single().FulfillmentStatus==OrderItemFulfillmentStatus.Shipped,
                    "partial transfer retains original invoice number, shipped line and original owner ("+mode+")");
                check(child.TotalAmount==20000&&child.Order!.CustomerId==to.Id&&child.Order.Items.Single().Id==second.Id&&child.Order.ShippedAt is null&&child.Order.Status!=OrderStatus.Shipped&&child.Status==(mode=="paid"?InvoiceStatus.Paid:InvoiceStatus.Issued),
                    "selected unshipped line moves to an independent invoice with preserved payment state ("+mode+")");
                check(await db.SalesListRequests.AsNoTracking().AnyAsync(r=>r.Id==selectedRequest.Id&&r.TelegramUserId==to.TelegramId)&&await db.SalesListRequests.AsNoTracking().AnyAsync(r=>r.Id==first.SourceSalesListRequestId&&r.TelegramUserId==from.TelegramId),"only selected sales-list identity moves; retained shipped recipient stays unchanged ("+mode+")");
                var after=await Debts();check(after==(before.From-(20000-paid),before.To+(20000-paid)),"partial transfer moves only selected outstanding balance ("+mode+")");
                var payments=await db.Payments.AsNoTracking().Where(p=>p.OrderId==multi.OrderId||p.OrderId==moved.OrderId).ToArrayAsync();
                var fullPaid=mode=="paid"?50500:mode=="partial"?10000:0;
                check(payments.Sum(p=>p.Amount)==fullPaid&&payments.Where(p=>p.OrderId==moved.OrderId).Sum(p=>p.Amount)==paid&&payments.Select(p=>p.TransactionId).Distinct().Count()==payments.Length,
                    "payment allocations preserve exact total receipts and unique transaction identifiers ("+mode+")");
                check((await service.ExecuteAsync(op,preview,Owner)).InvoiceId==moved.InvoiceId&&await Debts()==after,"partial transfer retry is idempotent and cannot split or charge twice ("+mode+")");
                var revision=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==multi.OrderId&&n.Channel==InvoiceRevisionService.Channel);
                var revisionData=JsonSerializer.Deserialize<InvoiceRevisionService.Progress>(revision.Payload)!;
                check(await Call("ProcessInvoiceRevisionAsync",revision,CancellationToken.None) is int,"old multi-item invoice stays until corrected retained-items PDF is delivered ("+mode+")");
                await Deliver(revisionData.DeliveryEvent,fromChat);
                await db.NotificationOutbox.Where(n=>revisionData.TelegramEvents.Contains(n.Id)).ExecuteUpdateAsync(u=>u.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
                check(await Call("ProcessInvoiceRevisionAsync",revision,CancellationToken.None) is int,"old complete invoice is retained until both recipients have their correct replacement documents ("+mode+")");
                await Deliver(moved.DeliveryEvent,toChat);await db.NotificationOutbox.Where(n=>moved.TelegramEvents.Contains(n.Id)).ExecuteUpdateAsync(u=>u.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
                check(await Follow(moved)is null&&(await Progress(moved)).Complete,"partial transfer delivers separate PDFs, revises payments report and completes cleanup ("+mode+")");
                check(await Call("ProcessInvoiceRevisionAsync",revision,CancellationToken.None)is null&&JsonSerializer.Deserialize<InvoiceRevisionService.Progress>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==revision.Id)).Payload)!.Complete,
                    "old source bundle is cleaned only after separate retained and transferred PDF delivery ("+mode+")");
                if(mode=="manual")
                {
                    var reportId=serial*100;var beforeRefresh=sent.Edits.Last(e=>e.Message==reportId).Text;
                    await Call("RefreshManualPaymentTrackingReportAsync",moved.InvoiceId,Owner,CancellationToken.None);
                    check(beforeRefresh.Contains("@"+from.Username)&&beforeRefresh.Contains("@"+to.Username)&&sent.Edits.Last(e=>e.Message==reportId).Text.Contains("@"+from.Username)&&sent.Edits.Last(e=>e.Message==reportId).Text.Contains("@"+to.Username),
                        "manual invoice report and later refresh retain both source and recipient allocations together");
                }

            }
            var ui=await Create();const string prefix="invoiceadmin:transfer:";
            Task Select(string action,long actor=Owner,string type="private")=>(Task)typeof(TelegramWebhookController).GetMethod("HandleInvoiceTransferCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramCallbackQuery("fixture",new(actor,null),new TelegramMessage(null,new(actor,type),new(actor,null),null,MessageId:1),prefix+action),CancellationToken.None])!;
            Task<object?> Input(string caption)=>Call("TryHandleInvoiceTransferMessageAsync",new TelegramMessage(null,new(Owner,"private"),new(Owner,null),null,Caption:caption),CancellationToken.None);
            await Select("start",Owner+1);check(sent.Texts.Last().Contains("ادمین"),"invoice transfer menu rejects customers");
            await Select("start",Owner,"supergroup");check(sent.Texts.Last().Contains("خصوصی"),"invoice transfer cannot be started from a group");
            await Select("start");await Input(Caption(ui));check(sent.Texts.Last().Contains("آیتم‌های موردنظر"),"transfer wizard accepts forwarded captions and displays per-item selection before recipient");
            var all=sent.Buttons.Last(b=>b.CallbackData?.EndsWith(":all")==true);await Select(all.CallbackData![prefix.Length..]);
            var selectionDone=sent.Buttons.Last(b=>b.CallbackData?.EndsWith(":done")==true);await Select(selectionDone.CallbackData![prefix.Length..]);
            check(sent.Texts.Last().Contains("آیدی انتقال‌گیرنده"),"selected transfer items advance to recipient entry");
            await Input("@"+to.Username);var confirm=sent.Buttons.Last(b=>b.CallbackData?.StartsWith(prefix+"confirm:")==true);
            check(sent.Texts.Last().Contains("پیش‌نمایش انتقال")&&sent.Texts.Last().Contains("ماندهٔ بدهی")&&Encoding.UTF8.GetByteCount(confirm.CallbackData!)<=64&&await db.Orders.AnyAsync(o=>o.Id==ui.OrderId&&o.CustomerId==from.Id),"administrator sees exact source, recipient, preserved payment and balance preview before any transfer");
            await Select("cancel");await Select(confirm.CallbackData![prefix.Length..]);
            check(await db.Orders.AnyAsync(o=>o.Id==ui.OrderId&&o.CustomerId==from.Id),"cancelled transfer confirmation cannot later move invoice ownership");
        }
        finally
        {
            db.ChangeTracker.Clear();orders.AddRange(await db.Orders.Where(o=>o.CustomerId==from.Id||o.CustomerId==to.Id).Select(o=>o.Id).ToArrayAsync());await db.NotificationOutbox.Where(n=>n.OrderId!=null&&orders.Contains(n.OrderId.Value)||n.CustomerId==from.Id||n.CustomerId==to.Id).ExecuteDeleteAsync();
            await db.OrderArtifacts.Where(a=>orders.Contains(a.OrderId)).ExecuteDeleteAsync();await db.Payments.Where(p=>orders.Contains(p.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(x=>orders.Contains(x.OrderId)).ExecuteDeleteAsync();await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatchSalesLists.Where(l=>batches.Contains(l.InvoiceIssuanceBatchId)).ExecuteDeleteAsync();await db.InvoiceIssuanceBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>lists.Contains(r.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>lists.Contains(l.Id)).ExecuteDeleteAsync();
            await db.CustomerTelegramGroups.Where(g=>g.CustomerId==from.Id||g.CustomerId==to.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==from.Id||c.Id==to.Id).ExecuteDeleteAsync();await db.Bottles.Where(b=>b.Id==bottle.Id).ExecuteDeleteAsync();
        }
    }
}

public class InvoiceTransferSender:DispatchProxy
{
    public List<string> Texts=[];public List<TelegramInlineButton> Buttons=[];public List<(string Chat,long Message,string Text)> Edits=[];
    public List<(string Chat,long Message)> Deleted=[];public List<(string Chat,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Buttons)> Documents=[];
    public long? FailDelete;private long next=10000000;
    public TaskCompletionSource<bool>? DocumentStarted,DocumentRelease;
    private async Task<TelegramSendResult> WaitDocument(TelegramSendResult result)
    {DocumentStarted!.TrySetResult(true);await DocumentRelease!.Task;return result;}
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="IsChatAdministratorAsync")return Task.FromResult(false);
        if(method.Name=="DeleteMessageAsync")
        {var target=((string)args![0]!, (long)args[1]!);if(target.Item2==FailDelete)return Task.FromResult(new TelegramSendResult(false,"fixture temporary delete failure"));Deleted.Add(target);return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name=="EditTextWithKeyboardAsync")Edits.Add(((string)args![0]!, (long)args[1]!, (string)args[2]!));
        if(method.Name=="SendDocumentWithKeyboardAsync")
        {
            Documents.Add(((string)args![0]!, (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[4]!));
            if(DocumentStarted is not null)return WaitDocument(new TelegramSendResult(true,MessageId:++next,ExternalFileId:"fixture-new-pdf"));
        }
        if(method.Name=="SendInlineKeyboardAsync")
        {Texts.Add((string)args![1]!);Buttons.AddRange(((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[2]!).SelectMany(r=>r));}
        if(method.Name=="AnswerCallbackAsync")Texts.Add(args![1]?.ToString()??"");
        return Task.FromResult(new TelegramSendResult(true,MessageId:++next,ExternalFileId:"fixture-new-pdf"));
    }
}
