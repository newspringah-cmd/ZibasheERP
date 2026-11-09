using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class PartialInvoiceCancellationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new Exception("Isolated SQL required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="partial_"+Guid.NewGuid().ToString("N")[..14];const long owner=771234567;const string chat="-1007773001";
        var customer=new Customer{Id=Guid.NewGuid(),Username=marker,TelegramId=marker,FullName=marker,Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var recipient=new Customer{Id=Guid.NewGuid(),Username=marker+"gift",TelegramId=marker+"gift",FullName=marker+"gift",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var bottle=new Bottle{Id=Guid.NewGuid(),Name=marker,VolumeMl=10,Type=BottleType.Fancy,SalePrice=2000,CreatedAt=DateTime.UtcNow};
        var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();
        var sources=Enumerable.Range(1,3).Select(n=>new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(20000000,30000000),
            PersianName="عطر تست انتخاب "+n,EnglishName=marker+n,PerfumeId=perfume,PricePerMl=12000,TotalVolume=100,ReservedVolume=100,
            Status=SalesListStatus.Invoiced,TelegramPhotoFileId="partial-photo-"+n,StoragePosition="test-shelf-"+n,CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow}).ToArray();
        db.Customers.AddRange(customer,recipient);db.Bottles.Add(bottle);db.SalesLists.AddRange(sources);
        db.CustomerTelegramGroups.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chat,IsActive=true,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow});
        await db.SaveChangesAsync();
        var serial=Random.Shared.Next(4000,4900);long message=9000;
        async Task<(Invoice Invoice,OrderItem[] Items,Guid OldEvent)> Fixture(bool gifts=false)
        {
            db.ChangeTracker.Clear();var c=await db.Customers.SingleAsync(c=>c.Id==customer.Id);
            var order=new Order{Id=Guid.NewGuid(),CustomerId=c.Id,Customer=c,Source=OrderSource.ManualInvoice,IsInventory=true,
                OrderNumber=marker+Guid.NewGuid().ToString("N"),Status=OrderStatus.ReadyToShip,CreatedAt=DateTime.UtcNow};
            for(var n=1;n<=3;n++)
            {
                var request=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=sources[n-1].Id,TelegramUserId=marker,TelegramUsername=marker,
                    VolumeMl=n,BottleId=bottle.Id,BottlePrice=2000,PerfumePricePerMl=10000,Status=SalesListRequestStatus.Invoiced,
                    ExternalReference=marker+Guid.NewGuid().ToString("N"),IsGift=gifts&&n>1,GiftRecipientTelegramUsername=gifts&&n>1?recipient.Username:null,
                    GiftRecipientTelegramUserId=gifts&&n>1?recipient.TelegramId:null,CreatedAt=DateTime.UtcNow};
                db.SalesListRequests.Add(request);
                order.Items.Add(new(){Id=Guid.NewGuid(),RowNumber=n,OrderId=order.Id,SalesListId=sources[n-1].Id,PerfumeId=perfume,
                    SourceSalesListRequestId=request.Id,BottleId=bottle.Id,BottlePrice=2000,RequestedVolumeMl=n,ManualDescription=sources[n-1].PersianName,
                    PerfumePricePerMl=10000,PerfumeAmount=n*10000,LineTotal=n*10000+2000,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,
                    ShippingRequestId=Guid.NewGuid(),ShippingRequestedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow});
            }
            order.PerfumeTotal=60000;order.BottleTotal=6000;order.FinalAmount=66000;
            var invoice=new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,Order=order,InvoiceNumber="INV-261109-"+(++serial),
                Status=InvoiceStatus.Issued,PerfumeTotal=60000,BottleTotal=6000,TotalAmount=66000,IssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,
                TelegramInvoiceChatId=chat,TelegramInvoiceMessageId=++message,TelegramPaymentTrackingChatId="-1007773002",TelegramPaymentTrackingMessageId=++message,IsSentToCustomer=true};
            c.CurrentDebt+=66000;db.Orders.Add(order);db.Invoices.Add(invoice);
            var source=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=source,OrderId=order.Id,CustomerId=c.Id,Channel="N8n",EventType="InvoiceIssued",Recipient="n8n",
                Payload=JsonSerializer.Serialize(new{InvoiceId=invoice.Id,TotalAmount=66000,Customer=new{Id=c.Id},Delivery=new{Channel="TelegramGroup",ChatId=chat}}),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});
            await db.SaveChangesAsync();
            await InvoiceMessageLifecycle.RecordAsync(db,order.Id,source,chat,invoice.TelegramInvoiceMessageId!.Value,"InvoicePdf");
            foreach(var kind in new[]{"InvoiceGreeting","InvoicePerfumePhoto"})
            {
                var id=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=id,OrderId=order.Id,CustomerId=c.Id,Channel="Telegram",EventType=kind,Recipient=chat,Payload="{}",Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});
                await db.SaveChangesAsync();await InvoiceMessageLifecycle.RecordAsync(db,order.Id,id,chat,++message,kind);
            }
            db.OrderArtifacts.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,SourceEventId=source,Type=OrderArtifactType.InvoicePdf,FileUrl="https://example.invalid/old.pdf",CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            var items=order.Items.OrderBy(x=>x.RowNumber).ToArray();db.ChangeTracker.Clear();return(invoice,items,source);
        }
        string Caption(Invoice i)=>$"🔴🔴موجودی🔴🔴\n🧾 فاکتور عطر @{marker}\nشماره فاکتور: {i.InvoiceNumber}\nمبلغ قابل پرداخت: {i.TotalAmount} تومان";
        async Task<bool> Rejected(Func<Task> action){try{await action();return false;}catch(InvalidOperationException){return true;}}
        var service=new InvoiceCancellationService(db);
        try
        {
            var f=await Fixture();var initial=await service.ResolveForSelectionAsync(Caption(f.Invoice),false);
            check(initial.Lines.Select(l=>l.ItemId).SequenceEqual(f.Items.Select(i=>i.Id)),"partial cancellation item numbers follow the invoice row order");
            check(await Rejected(async()=>{await service.SelectAsync(initial,false,[]);})&&
                await Rejected(async()=>{await service.SelectAsync(initial,false,[Guid.NewGuid()]);})&&
                await Rejected(async()=>{await service.SelectAsync(initial,false,[f.Items[0].Id,f.Items[0].Id]);}),"empty, foreign and duplicate item selections cannot reach financial cancellation");
            var selected=await service.SelectAsync(initial,false,[f.Items[0].Id,f.Items[2].Id]);
            check(selected.Partial&&selected.Amount==44000&&selected.RemainingAmount==22000&&selected.Lines.Length==2,"two selected rows preview their exact reversal and the retained invoice amount");
            var debt=await db.Customers.Where(c=>c.Id==customer.Id).Select(c=>c.CurrentDebt).SingleAsync();var operation=Guid.NewGuid();
            var result=await service.ExecuteAsync(operation,selected,false,new Dictionary<Guid,decimal>(),owner,"@zibasheperfume_mojodi");db.ChangeTracker.Clear();
            check(result.SourceInvoiceId==f.Invoice.Id&&result.InvoiceId!=f.Invoice.Id&&result.ReversedAmount==44000&&result.RemainingAmount==22000&&result.Offers.Length==0,
                "partial deletion archives only the selected financial lines without creating stock");
            var kept=await db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Items).SingleAsync(i=>i.Id==f.Invoice.Id);
            check(kept.InvoiceNumber==f.Invoice.InvoiceNumber&&kept.Status==InvoiceStatus.Issued&&kept.TotalAmount==22000&&kept.Order!.FinalAmount==22000&&
                kept.Order.Status==OrderStatus.ReadyToShip&&kept.Order.Items.Single().Id==f.Items[1].Id&&kept.Order.Items.Single().ShippingRequestId==f.Items[1].ShippingRequestId,
                "retained rows keep the original invoice number, ready status and parcel membership with exact recomputed totals");
            check(await db.Customers.Where(c=>c.Id==customer.Id).Select(c=>c.CurrentDebt).SingleAsync()==debt-44000&&
                await db.SalesListRequests.AnyAsync(r=>r.Id==f.Items[1].SourceSalesListRequestId&&r.Status==SalesListRequestStatus.Invoiced)&&
                await db.SalesListRequests.Where(r=>r.Id==f.Items[0].SourceSalesListRequestId||r.Id==f.Items[2].SourceSalesListRequestId).AllAsync(r=>r.Status==SalesListRequestStatus.Cancelled),
                "only selected debt and requests are reversed while retained requests remain invoiced");
            var retry=await service.ExecuteAsync(operation,selected,false,new Dictionary<Guid,decimal>(),owner,"@zibasheperfume_mojodi");db.ChangeTracker.Clear();
            check(retry.InvoiceId==result.InvoiceId&&await db.Customers.Where(c=>c.Id==customer.Id).Select(c=>c.CurrentDebt).SingleAsync()==debt-44000,
                "repeated partial confirmation cannot duplicate cancellation documents or reverse debt twice");
            check(await db.NotificationOutbox.AnyAsync(n=>n.Id==f.OldEvent&&n.IsDeleted)&&await db.OrderArtifacts.AnyAsync(a=>a.SourceEventId==f.OldEvent&&a.IsDeleted),
                "superseded invoice events and PDF artifacts are invalidated so late old callbacks cannot resend stale amounts");
            var work=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==f.Invoice.OrderId&&n.Channel==InvoiceRevisionService.Channel);
            var progress=JsonSerializer.Deserialize<InvoiceRevisionService.Progress>(work.Payload)!;
            var delivery=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==progress.DeliveryEvent);var body=JsonNode.Parse(delivery.Payload)!;
            check(body["InvoiceId"]!.GetValue<Guid>()==f.Invoice.Id&&body["TotalAmount"]!.GetValue<decimal>()==22000&&body["IsInventory"]!.GetValue<bool>()&&
                body["Items"]!.AsArray().Count==1&&body["Items"]![0]!["RequestedVolumeMl"]!.GetValue<decimal>()==2,
                "replacement PDF and caption contain only retained items, corrected prices and the inventory marker");
            check(!await db.NotificationOutbox.AnyAsync(n=>n.OrderId==f.Invoice.OrderId&&n.CreatedAt>=progress.CreatedAt&&(n.EventType.Contains("DecantList")||n.EventType.Contains("Label"))),
                "amending inventory invoices does not generate decant or label production copies");
            var sender=DispatchProxy.Create<ITelegramMessageSender,RevisionCapture>();var captured=(RevisionCapture)(object)sender;
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{NewPaymentsChatId="-1007773002",InvoiceFailureChatId="-1007773003"})})
                typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
            Task<int?> Follow(NotificationOutbox job)=>(Task<int?>)typeof(TelegramWebhookController).GetMethod("ProcessInvoiceRevisionAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[job,CancellationToken.None])!;
            Task<int?> Revision()=>Follow(work);
            check(await Revision()==5&&captured.Deleted.Count==0,"old invoice bundle stays intact while its corrected PDF has not been delivered");
            await db.NotificationOutbox.Where(n=>n.Id==progress.DeliveryEvent).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Failed));
            check(await Revision()==5&&captured.Deleted.Count==0,"failed replacement delivery cannot delete the customer's existing invoice");
            var cancelledOrderId=await db.Invoices.Where(i=>i.Id==result.InvoiceId).Select(i=>i.OrderId).SingleAsync();
            var reportJob=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==cancelledOrderId&&n.Channel==InvoiceRestockTracking.Channel);
            await (Task)typeof(TelegramWebhookController).GetMethod("ProcessInvoiceRestockTrackingAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[reportJob,CancellationToken.None])!;
            check(captured.Edits.Single().Message==f.Invoice.TelegramPaymentTrackingMessageId&&captured.Edits.Single().Text.Contains("22,000")&&
                captured.Edits.Single().Text.Contains("🔵 آیتم‌های حذف‌شده")&&captured.Edits.Single().Text.Contains(InvoiceRestockTracking.UnknownCaption),
                "manual payments keep the retained invoice and blue cancellation history together in the original report");
            var artifactApi=new N8nIntegrationsController(null!,db,Options.Create(new TelegramOptions()),sender);
            async Task<IActionResult> Deliver(Guid source)
            {
                var bytes=System.Text.Encoding.UTF8.GetBytes("%PDF isolated synthetic invoice");using var stream=new MemoryStream(bytes);
                return await artifactApi.SendTelegramInvoice(source,chat,Caption(kept)+"\n🧴 "+sources[1].PersianName,"invoice.pdf",new FormFile(stream,0,bytes.Length,"document","invoice.pdf"),default);
            }
            check(await Deliver(f.OldEvent) is ConflictObjectResult&&captured.Documents.Count==0,"a late callback for the superseded full invoice cannot deliver the old PDF or caption");
            await db.CustomerTelegramGroups.Where(g=>g.CustomerId==customer.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,false));
            check(await Deliver(progress.DeliveryEvent) is ConflictObjectResult&&captured.Documents.Count==0,"corrected invoice delivery refuses a customer group that lost its active owner binding");
            await db.CustomerTelegramGroups.Where(g=>g.CustomerId==customer.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,true));
            check(await Deliver(progress.DeliveryEvent) is OkObjectResult&&captured.Documents.Single().StartsWith("🔴🔴موجودی🔴🔴")&&
                captured.Documents.Single().Contains("22000")&&captured.Documents.Single().Contains(sources[1].PersianName),
                "when the old invoice cannot be edited the corrected retained invoice is sent as a new document with its new caption and inventory heading");
            await db.NotificationOutbox.Where(n=>progress.TelegramEvents.Contains(n.Id)||n.Id==progress.DeliveryEvent).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            captured.FailNextDelete=true;check(await Rejected(async()=>{await Revision();}),"cleanup failure remains retryable after successful replacement delivery");
            check(await Revision() is null&&captured.Deleted.Distinct().Count()==3&&!captured.Deleted.Contains(100001),
                "cleanup removes only the captured old greeting, photo and PDF and never the newly delivered invoice");
            await db.NotificationOutbox.Where(n=>n.Id==work.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            var currentCaption=Caption(kept);check((await service.ResolveAsync(currentCaption,false)).Amount==22000,"the remaining invoice can be found using its new caption after revision completes");

            var gift=await Fixture(true);var giftEvents=new List<(Guid Id,int Row)>();
            for(var row=2;row<=3;row++)
            {
                var id=Guid.NewGuid();giftEvents.Add((id,row));
                db.NotificationOutbox.Add(new(){Id=id,OrderId=gift.Invoice.OrderId,CustomerId=recipient.Id,Channel="N8n",EventType="InvoiceIssued",Recipient="n8n",CreatedAt=DateTime.UtcNow,
                    Payload=JsonSerializer.Serialize(new{GiftDeliveryRole="Recipient",Items=new[]{new{RowNumber=row,PerfumePersianName=sources[row-1].PersianName,RequestedVolumeMl=(decimal)row}}})});
            }
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var all=await service.ResolveForSelectionAsync(Caption(gift.Invoice),true);var one=await service.SelectAsync(all,true,[gift.Items[1].Id]);
            var stock=await service.ExecuteAsync(Guid.NewGuid(),one,true,one.Lines.ToDictionary(l=>l.ItemId,l=>l.CurrentPrice),owner,"@zibasheperfume_mojodi");db.ChangeTracker.Clear();
            check(stock.Offers.Length==1&&await db.SalesLists.AnyAsync(l=>l.Id==stock.Offers[0]&&l.SourceOrderItemId==gift.Items[1].Id&&l.TotalVolume==2)&&
                await db.Invoices.AnyAsync(i=>i.Id==gift.Invoice.Id&&i.Status==InvoiceStatus.Issued&&i.TotalAmount==44000),
                "partial return publishes only the selected package while two unselected items remain in the original invoice");
            check(await db.NotificationOutbox.AnyAsync(n=>n.Id==giftEvents[0].Id&&n.IsDeleted)&&
                await db.NotificationOutbox.AnyAsync(n=>n.Id==giftEvents[1].Id&&!n.IsDeleted&&n.Status==NotificationOutboxStatus.Pending)&&
                await db.SalesListRequests.AnyAsync(r=>r.Id==gift.Items[2].SourceSalesListRequestId&&r.Status==SalesListRequestStatus.Invoiced),
                "cancelling one gift invalidates only that gift's stale delivery while retained gifts and notifications remain unchanged");
            var stockOrder=await db.Invoices.Where(i=>i.Id==stock.InvoiceId).Select(i=>i.OrderId).SingleAsync();
            var stockJob=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==stockOrder&&n.Channel==InvoiceRestockTracking.Channel);
            Task StockReport()=>(Task)typeof(TelegramWebhookController).GetMethod("ProcessInvoiceRestockTrackingAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[stockJob,CancellationToken.None])!;
            check(await Rejected(StockReport)&&captured.Edits.Last().Text.Contains(InvoiceRestockTracking.UnknownCaption),"partial stock return remains unknown while actual channel publication is pending");
            await db.SalesLists.Where(l=>stock.Offers.Contains(l.Id)).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramMessageId,77001L).SetProperty(l=>l.TelegramChannelId,"@zibasheperfume_mojodi"));
            await StockReport();
            check(captured.Edits.Last().Text.Contains("44,000")&&captured.Edits.Last().Text.Contains(InvoiceRestockTracking.Caption)&&
                (await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[stockOrder])).Contains(stock.InvoiceId),
                "successful publication updates only the cancelled portion to stock-transferred while retaining the active invoice balance");
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(await db.Invoices.AsNoTracking().SingleAsync(i=>i.Id==gift.Invoice.Id)),false);}),
                "another accounting change waits until corrected invoice delivery and cleanup finish");
            var giftWork=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==gift.Invoice.OrderId&&n.Channel==InvoiceRevisionService.Channel);
            var giftProgress=JsonSerializer.Deserialize<InvoiceRevisionService.Progress>(giftWork.Payload)!;
            await InvoiceMessageLifecycle.RecordAsync(db,gift.Invoice.OrderId,giftProgress.DeliveryEvent,chat,100002,"InvoicePdf");await db.SaveChangesAsync();
            await db.NotificationOutbox.Where(n=>giftProgress.TelegramEvents.Contains(n.Id)).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            captured.RestrictNextDelete=true;
            check(await Follow(giftWork) is null&&await db.NotificationOutbox.AnyAsync(n=>n.OrderId==gift.Invoice.OrderId&&n.EventType=="InvoiceRevisionCleanupNeedsManualAction"&&n.Recipient=="-1007773003"),
                "an undeletable old message does not block corrected invoice delivery and queues a precise manual-cleanup alert");
            var completed=JsonSerializer.Deserialize<InvoiceRevisionService.Progress>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==giftWork.Id)).Payload)!;
            check(completed.Complete&&completed.ManualCleanup?.Length==1,"old-message deletion restrictions are recorded separately from successful automatic cleanup");

            var ui=await Fixture();var uiSender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var uiCaptured=(MarketingSender)(object)uiSender;
            var uiController=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)is not null)field.SetValue(uiController,Activator.CreateInstance(field.FieldType));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",uiSender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),ReadyInventoryChannelId="@zibasheperfume_mojodi"}),("_adminActivityContext",new AdminActivityContext())})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(uiController,value);
            const string prefix="invoiceadmin:cancelinvoice:";
            Task Select(string action)=>(Task)typeof(TelegramWebhookController).GetMethod("HandleInvoiceCancellationCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(uiController,[new TelegramCallbackQuery("partial-fixture",new(owner,null),new TelegramMessage(null,new(owner,"private"),new(owner,null),null,MessageId:42),prefix+action),CancellationToken.None])!;
            Task<bool> Input()=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleInvoiceCancellationMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(uiController,[new TelegramMessage(Caption(ui.Invoice),new(owner,"private"),new(owner,null),null,MessageId:43),CancellationToken.None])!;
            await Select("start");await Input();
            check(uiCaptured.Texts.Last().Contains("تعداد انتخاب‌شده: 0 از 3")&&uiCaptured.Buttons.Any(b=>b.StartsWith(prefix+"all:")),"multi-item invoice cancellation asks for all items or explicit individual selection before any write");
            var stale=uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"next:"));
            await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"pick:0:"))[prefix.Length..]);
            await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"pick:2:"))[prefix.Length..]);
            await Select(stale[prefix.Length..]);check(await db.Invoices.AnyAsync(i=>i.Id==ui.Invoice.Id&&i.TotalAmount==66000),"stale selection callbacks cannot confirm a different or newer selection");
            await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"next:"))[prefix.Length..]);
            check(uiCaptured.Texts.Any(t=>t.Contains("44,000"))&&uiCaptured.Texts.Last().Contains("22,000")&&uiCaptured.Texts.Last().Contains("فقط آیتم‌های بالا"),"partial confirmation shows the selected reversal and exact retained invoice total");
            await Select("choose");await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"all:"))[prefix.Length..]);
            await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"next:"))[prefix.Length..]);
            check(uiCaptured.Texts.Last().Contains("کل فاکتور لغو")&&await db.Invoices.AnyAsync(i=>i.Id==ui.Invoice.Id&&i.Status==InvoiceStatus.Issued),"select-all retains whole-invoice cancellation behavior and still requires final confirmation");
            await Select("cancel");
            await Select("return-start");await Input();await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"pick:1:"))[prefix.Length..]);
            await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"next:"))[prefix.Length..]);
            check(uiCaptured.Texts.Last().Contains("قیمت روز هر میل")&&uiCaptured.Texts.Last().Contains(sources[1].PersianName),"return-to-stock compares the current price only for the selected invoice item");
            await Select("accept-price:0");await Select(uiCaptured.Buttons.Last(b=>b.StartsWith(prefix+"confirm:"))[prefix.Length..]);
            check(await db.Invoices.AnyAsync(i=>i.Id==ui.Invoice.Id&&i.Status==InvoiceStatus.Issued&&i.TotalAmount==44000)&&
                await db.SalesLists.CountAsync(l=>l.SourceOrderItemId==ui.Items[1].Id)==1&&uiCaptured.Texts.Last().Contains("سایر آیتم‌ها باقی ماندند"),
                "the administrator can finish a one-item stock return from a three-item invoice while keeping the other two invoiced");
        }
        finally
        {
            db.ChangeTracker.Clear();var orderIds=await db.Orders.Where(o=>o.CustomerId==customer.Id).Select(o=>o.Id).ToArrayAsync();
            var itemIds=await db.OrderItems.Where(i=>orderIds.Contains(i.OrderId)).Select(i=>i.Id).ToArrayAsync();
            var offers=await db.SalesLists.Where(l=>l.SourceOrderItemId!=null&&itemIds.Contains(l.SourceOrderItemId.Value)).Select(l=>l.Id).ToArrayAsync();
            var requestIds=await db.OrderItems.Where(i=>orderIds.Contains(i.OrderId)&&i.SourceSalesListRequestId!=null).Select(i=>i.SourceSalesListRequestId!.Value).ToArrayAsync();
            await db.NotificationOutbox.Where(n=>n.OrderId!=null&&orderIds.Contains(n.OrderId.Value)||offers.Contains(n.Id)).ExecuteDeleteAsync();
            await db.OrderArtifacts.Where(a=>orderIds.Contains(a.OrderId)).ExecuteDeleteAsync();await db.Payments.Where(p=>orderIds.Contains(p.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(i=>orderIds.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Invoices.Where(i=>orderIds.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orderIds.Contains(o.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>requestIds.Contains(r.Id)).ExecuteDeleteAsync();
            await db.InventoryPositionChanges.Where(p=>offers.Contains(p.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>offers.Contains(l.Id)||sources.Select(s=>s.Id).Contains(l.Id)).ExecuteDeleteAsync();
            await db.CustomerTelegramGroups.Where(g=>g.CustomerId==customer.Id||g.CustomerId==recipient.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id||c.Id==recipient.Id).ExecuteDeleteAsync();await db.Bottles.Where(b=>b.Id==bottle.Id).ExecuteDeleteAsync();
        }
    }
}
public class RevisionCapture:DispatchProxy
{
    public List<long> Deleted=[];public List<(long Message,string Text)> Edits=[];public List<string> Documents=[];public bool FailNextDelete;public bool RestrictNextDelete;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="DeleteMessageAsync")
        {
            if(FailNextDelete){FailNextDelete=false;return Task.FromResult(new TelegramSendResult(false,"fixture delete timeout"));}
            if(RestrictNextDelete){RestrictNextDelete=false;return Task.FromResult(new TelegramSendResult(false,"Bad Request: message can't be deleted"));}
            Deleted.Add((long)args![1]!);return Task.FromResult(new TelegramSendResult(true));
        }
        if(method.Name=="EditTextWithKeyboardAsync"){Edits.Add(((long)args![1]!, (string)args[2]!));return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name=="SendDocumentWithKeyboardAsync"){Documents.Add((string)args![3]!);return Task.FromResult(new TelegramSendResult(true,MessageId:100000+Documents.Count,ExternalFileId:"synthetic-revision-pdf"));}
        throw new NotSupportedException(method.Name);
    }
}
