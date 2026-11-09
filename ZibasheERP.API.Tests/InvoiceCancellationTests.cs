using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;
using ZibasheERP.Infrastructure.Repositories;

public static class InvoiceCancellationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var parsed=InvoiceCaptionIdentity.Parse("🔴🔴موجودی🔴🔴\n🧾 فاکتور عطر @example_123\nشماره فاکتور: INV-261102-1234\nمبلغ قابل پرداخت: ۱۲۳٬۴۵۶ تومان");
        check(parsed.Inventory&&parsed.Amount==123456&&parsed.Number=="INV-261102-1234","caption parsing handles Persian amounts, invoice numbers and inventory marker without interpreting bank cards");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=771234567;var marker="cancel_"+Guid.NewGuid().ToString("N")[..15];
        var customer=new Customer{Id=Guid.NewGuid(),Username=marker,TelegramId=marker,FullName=marker,Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var bottle=new Bottle{Id=Guid.NewGuid(),Name=marker,VolumeMl=10,Type=BottleType.Fancy,SalePrice=5000,CreatedAt=DateTime.UtcNow};
        var source=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(7000000,7900000),EnglishName=marker,PersianName="عطر تست لغو",PerfumeId=await db.Perfumes.Select(p=>p.Id).FirstAsync(),PricePerMl=10000,TotalVolume=100,ReservedVolume=100,Status=SalesListStatus.Invoiced,CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow,TelegramPhotoFileId="fixture-photo",StoragePosition="fixture-shelf"};
        db.Customers.Add(customer);db.Bottles.Add(bottle);db.SalesLists.Add(source);await db.SaveChangesAsync();
        var orders=new List<Guid>();var extraLists=new List<Guid>();var serial=1200;var service=new InvoiceCancellationService(db);
        async Task<Invoice> Invoice(bool inventory=false,params decimal[] volumes)
        {
            db.ChangeTracker.Clear();var o=new Order{Id=Guid.NewGuid(),CustomerId=customer.Id,OrderNumber=marker+Guid.NewGuid().ToString("N"),Status=OrderStatus.ReadyToShip,IsInventory=inventory,CreatedAt=DateTime.UtcNow};
            foreach(var volume in volumes)
            {
                var req=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=source.Id,TelegramUserId=marker,TelegramUsername=marker,VolumeMl=volume,BottleId=bottle.Id,BottlePrice=5000,PerfumePricePerMl=7000,Status=SalesListRequestStatus.Invoiced,CreatedAt=DateTime.UtcNow,ExternalReference=marker+Guid.NewGuid().ToString("N")};
                db.SalesListRequests.Add(req);
                o.Items.Add(new OrderItem{Id=Guid.NewGuid(),SalesListId=source.Id,PerfumeId=source.PerfumeId,SourceSalesListRequestId=req.Id,BottleId=bottle.Id,BottlePrice=5000,RequestedVolumeMl=volume,PerfumePricePerMl=7000,PerfumeAmount=7000*volume,LineTotal=7000*volume+5000,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,ShippingRequestId=Guid.NewGuid(),ShippingRequestedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow});
            }
            o.PerfumeTotal=o.Items.Sum(x=>x.PerfumeAmount);o.BottleTotal=o.Items.Sum(x=>x.BottlePrice);o.FinalAmount=o.PerfumeTotal+o.BottleTotal;
            db.Orders.Add(o);var i=new Invoice{Id=Guid.NewGuid(),OrderId=o.Id,InvoiceNumber="INV-261102-"+(++serial),TotalAmount=o.FinalAmount,PerfumeTotal=o.PerfumeTotal,BottleTotal=o.BottleTotal,Status=InvoiceStatus.Issued,IssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow};
            db.Invoices.Add(i);var c=await db.Customers.SingleAsync(c=>c.Id==customer.Id);c.CurrentDebt+=i.TotalAmount;
            await db.SaveChangesAsync();orders.Add(o.Id);db.ChangeTracker.Clear();return i;
        }
        string Caption(Invoice i,bool numbered=true,bool inventory=false)
        {
            var day=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(i.IssuedAt,"Asia/Tehran");var pc=new PersianCalendar();
            var line=numbered?$"شماره فاکتور: {i.InvoiceNumber}\n":$"تاریخ: {pc.GetYear(day)}/{pc.GetMonth(day):00}/{pc.GetDayOfMonth(day):00}\n🧴 {source.PersianName}\nمقدار: 3 میل\n";
            return (inventory?"🔴🔴موجودی🔴🔴\n":"")+$"🧾 فاکتور عطر @{marker}\n{line}💰 مبلغ قابل پرداخت: {i.TotalAmount:N0} تومان";
        }
        async Task<bool> Rejected(Func<Task> action){try{await action();return false;}catch(InvalidOperationException){db.ChangeTracker.Clear();return true;}}
        async Task<decimal> Debt()=>await db.Customers.AsNoTracking().Where(c=>c.Id==customer.Id).Select(c=>c.CurrentDebt).SingleAsync();
        try
        {
            var plain=await Invoice(false,3m);var preview=await service.ResolveAsync(Caption(plain,false),false);
            check(preview.InvoiceId==plain.Id&&preview.Lines.Single().InvoicePrice==7000&&preview.Lines.Single().CurrentPrice==10000,"legacy caption resolves exact customer, date, perfume and quantity and compares original and current perfume prices");
            var before=await Debt();var op=Guid.NewGuid();
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),OrderId=plain.OrderId,CustomerId=customer.Id,Channel="Telegram",EventType="InvoiceIssued",Payload="{}",CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            var result=await service.ExecuteAsync(op,preview,false,new Dictionary<Guid,decimal>(),owner,"",default);
            check(await Debt()==before-plain.TotalAmount&&result.Offers.Length==0&&await db.Invoices.AsNoTracking().AnyAsync(i=>i.Id==plain.Id&&i.Status==InvoiceStatus.Cancelled&&i.TotalAmount==plain.TotalAmount),"plain cancellation reverses debt exactly once and preserves original invoice amount without creating stock");
            check(await db.OrderItems.AsNoTracking().Where(x=>x.OrderId==plain.OrderId).AllAsync(x=>x.IsDeleted&&x.ShippingRequestId==null)&&await db.NotificationOutbox.AnyAsync(n=>n.OrderId==plain.OrderId&&n.EventType=="InvoiceIssued"&&n.Status==NotificationOutboxStatus.Processed),"cancelled invoice items leave shipping and unsent invoice notification jobs are suppressed");
            var replay=await service.ExecuteAsync(op,preview,false,new Dictionary<Guid,decimal>(),owner,"");
            check(replay.InvoiceId==result.InvoiceId&&await Debt()==before-plain.TotalAmount,"durable replay receipt prevents duplicate financial reversal after retry");
            check(await db.Invoices.AnyAsync(i=>i.Id==plain.Id&&i.DeliveryStatus==InvoiceDeliveryStatus.Cancelled),"cancelled invoice leaves the undelivered invoice queue instead of requesting payment again");
            await db.Invoices.Where(i=>i.Id==plain.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.DeliveryStatus,InvoiceDeliveryStatus.NeedsManualAction));db.ChangeTracker.Clear();
            var reconnectSender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();
            var tracker=new TelegramGroupMembershipTracker(db,reconnectSender,NullLogger<TelegramGroupMembershipTracker>.Instance);
            var queued=await (Task<int>)typeof(TelegramGroupMembershipTracker).GetMethod("QueueUndeliveredInvoicesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tracker,[customer.Id,"-100123456",CancellationToken.None])!;
            check(queued==0,"group reconnection cannot revive a cancelled legacy invoice even if its old delivery flag says manual action");
            await db.Invoices.Where(i=>i.Id==plain.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.DeliveryStatus,InvoiceDeliveryStatus.Cancelled));
            check((await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==source.Id)).ReservedVolume==100,"cancelling purchased invoice stock does not reopen old list capacity");

            var a=await Invoice(false,3m);var b=await Invoice(true,3m);
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(a,false),false);}),"ambiguous same-day same-amount captions are rejected rather than selecting the latest or unpaid invoice");
            check((await service.ResolveAsync(Caption(b,false,true),false)).InvoiceId==b.Id,"inventory caption marker disambiguates inventory and ordinary invoices with the same amount");
            check((await service.ResolveAsync(Caption(a),false)).InvoiceId==a.Id,"explicit invoice number resolves duplicate legacy captions exactly");
            check((await service.ResolveAsync(Caption(a).Replace("@"+marker,""),false)).InvoiceId==a.Id,"numbered invoice caption remains usable when customer has no username in its heading");
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(a).Replace(marker,"another_customer"),false);}),"wrong customer cannot be cancelled by copying another invoice number");
            var paid=await Invoice(false,2m);await db.Invoices.Where(i=>i.Id==paid.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.Status,InvoiceStatus.Paid));
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(paid),false);}),"paid invoices require accounting reconciliation before cancellation");
            var partial=await Invoice(false,2m);var partialPreview=await service.ResolveAsync(Caption(partial),false);
            db.Payments.Add(new(){Id=Guid.NewGuid(),OrderId=partial.OrderId,Amount=1,Status=PaymentStatus.Confirmed,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            var partialDebt=await Debt();
            check(await Rejected(async()=>{await service.ExecuteAsync(Guid.NewGuid(),partialPreview,false,new Dictionary<Guid,decimal>(),owner,"");})&&await Debt()==partialDebt,"payment confirmed after preview prevents cancellation without any debt change");
            var shipped=await Invoice(false,2m);await db.OrderItems.Where(x=>x.OrderId==shipped.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ShippedAt,DateTime.UtcNow));
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(shipped),true);}),"shipped items cannot be returned to stock from a pasted invoice caption");

            var returned=await Invoice(true,3m,1.5m);var returnPreview=await service.ResolveAsync(Caption(returned),true);
            var changedPrices=returnPreview.Lines.ToDictionary(l=>l.ItemId,l=>9000m);
            var staleDebt=await Debt();await db.SalesLists.Where(l=>l.Id==source.Id).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.PricePerMl,12000m));
            check(await Rejected(async()=>{await service.ExecuteAsync(Guid.NewGuid(),returnPreview,true,changedPrices,owner,"@zibasheperfume_mojodi");})&&await Debt()==staleDebt,"market price change after transfer preview invalidates the approval atomically");
            returnPreview=await service.ResolveAsync(Caption(returned),true);
            check(returnPreview.Lines.All(l=>l.CurrentPrice==12000),"renewed transfer preview reads the current perfume price rather than historical invoice price");
            var returnOp=Guid.NewGuid();var transferred=await service.ExecuteAsync(returnOp,returnPreview,true,changedPrices,owner,"@zibasheperfume_mojodi");db.ChangeTracker.Clear();
            var offers=await db.SalesLists.AsNoTracking().Where(l=>transferred.Offers.Contains(l.Id)).ToArrayAsync();
            check(offers.Length==2&&offers.All(l=>l.IsInventoryOffer&&l.InventoryCode>=1000&&l.InventoryCode<=9999&&l.PricePerMl==9000&&l.FixedBottlePrice==5000&&l.Status==SalesListStatus.Open)&&offers.Sum(l=>l.TotalVolume)==4.5m,
                "whole invoice return creates independent four-digit ready packages with corrected per-ml price, original bottles and exact half-ml volumes");
            check(await db.NotificationOutbox.CountAsync(n=>transferred.Offers.Contains(n.Id)&&n.Channel=="MarketingPromotion"&&n.EventType=="ReadyInventoryPublication")==2&&await Debt()==staleDebt-returned.TotalAmount,"stock creation, durable photo publication jobs and debt reversal commit together");
            var transferredAgain=await service.ExecuteAsync(returnOp,returnPreview,true,changedPrices,owner,"@zibasheperfume_mojodi");
            check(transferredAgain.Offers.SequenceEqual(transferred.Offers)&&await db.SalesLists.CountAsync(l=>transferred.Offers.Contains(l.Id))==2,"retrying invoice-to-stock transfer reuses its result and cannot duplicate packages or publication jobs");
            check(await db.NotificationOutbox.CountAsync(n=>n.OrderId==returned.OrderId&&n.Channel==InvoiceRestockTracking.Channel)==1&&
                await db.NotificationOutbox.CountAsync(n=>n.OrderId==plain.OrderId&&n.Channel==InvoiceRestockTracking.Channel)==1,
                "both stock return and plain deletion atomically queue one durable payment-report refresh");
            check(!(await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[returned.OrderId])).Contains(returned.Id),"cancelled invoice is not labelled sent before inventory publication succeeds");
            await db.SalesLists.Where(l=>l.Id==transferred.Offers[0]).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramMessageId,6001L).SetProperty(l=>l.TelegramChannelId,"@zibasheperfume_mojodi"));
            check(!(await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[returned.OrderId])).Contains(returned.Id),"partial multi-item stock publication does not claim the whole invoice was sent");
            await db.SalesLists.Where(l=>l.Id==transferred.Offers[1]).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramMessageId,6002L).SetProperty(l=>l.TelegramChannelId,"@zibasheperfume_mojodi"));
            check((await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[returned.OrderId,plain.OrderId])).SetEquals([returned.Id]),"all exact returned packages must have bound publication receipts; unrelated cancelled invoices remain unmarked");
            check(await db.SalesListRequests.Where(r=>db.OrderItems.Any(x=>x.OrderId==returned.OrderId&&x.SourceSalesListRequestId==r.Id)).AllAsync(r=>r.Status==SalesListRequestStatus.Cancelled),"returned invoice cancels original requests while preserving gift and customer history");
            var oldStockId=Guid.NewGuid();extraLists.Add(oldStockId);
            await new ReadyInventoryService(db).CreateAsync(new(oldStockId,source.Id,source.PersianName,"fixture-photo",10000,1.5m,bottle.Id,5000,"old-stock-shelf",owner));
            var oldInvoice=await Invoice(true,1.5m);var oldBatch=new InvoiceIssuanceBatch{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,CreatedByTelegramUserId=owner.ToString()};
            db.InvoiceIssuanceBatches.Add(oldBatch);db.InvoiceIssuanceBatchSalesLists.Add(new(){InvoiceIssuanceBatchId=oldBatch.Id,SalesListId=oldStockId});await db.SaveChangesAsync();
            await db.Orders.Where(o=>o.Id==oldInvoice.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.InvoiceIssuanceBatchId,oldBatch.Id));
            await db.OrderItems.Where(x=>x.OrderId==oldInvoice.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SalesListId,oldStockId));
            await db.SalesListRequests.Where(r=>db.OrderItems.Any(x=>x.OrderId==oldInvoice.OrderId&&x.SourceSalesListRequestId==r.Id)).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.SalesListId,oldStockId));
            await db.SalesLists.Where(l=>l.Id==oldStockId).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.Status,SalesListStatus.Invoiced).SetProperty(l=>l.ReservedVolume,1.5m));db.ChangeTracker.Clear();
            var oldPreview=await service.ResolveAsync(Caption(oldInvoice,true,true),true);
            var restored=await service.ExecuteAsync(Guid.NewGuid(),oldPreview,true,oldPreview.Lines.ToDictionary(l=>l.ItemId,l=>l.CurrentPrice),owner,"@zibasheperfume_mojodi");db.ChangeTracker.Clear();
            check(restored.Offers.Single()!=oldStockId&&await db.InvoiceIssuanceBatchSalesLists.AnyAsync(l=>l.SalesListId==oldStockId&&l.InvoiceIssuanceBatchId==oldBatch.Id),"returning previously invoiced inventory keeps old billing history and creates an independent new offer");
            var publication=new InventoryOffersController(db,Options.Create(new TelegramOptions{ReadyInventoryChannelId="@zibasheperfume_mojodi"}),new InvoiceIssuanceService(db,DispatchProxy.Create<ISender,ReadyBillingSender>()));
            check(await publication.Publication(oldStockId,null,default)is ConflictResult,"old publication jobs cannot republish a cancelled inventory package");
            var reportService=new InvoiceIssuanceService(db,DispatchProxy.Create<ISender,ReadyBillingSender>());
            await reportService.SetPaymentTrackingMessageAsync(oldBatch.Id,oldStockId,"-1007770001",5555,default);
            var reportTransport=DispatchProxy.Create<ITelegramMessageSender,RestockTrackingSender>();var reportSent=(RestockTrackingSender)(object)reportTransport;
            var reportController=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var (field,value) in new(string,object)[]{("_db",db),("_sender",reportTransport),("_invoiceIssuanceService",reportService),("_options",new TelegramOptions{NewPaymentsChatId="-1007770001"})})
                typeof(TelegramWebhookController).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(reportController,value);
            var reportJob=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==oldInvoice.OrderId&&n.Channel==InvoiceRestockTracking.Channel);
            Task RefreshStockReport(NotificationOutbox job)=>(Task)typeof(TelegramWebhookController).GetMethod("ProcessInvoiceRestockTrackingAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(reportController,[job,CancellationToken.None])!;
            check(await Rejected(()=>RefreshStockReport(reportJob))&&reportSent.Edits.Count==1&&
                reportSent.Edits.Single().Text.Contains("🔵 1. @"+marker)&&reportSent.Edits.Single().Text.Contains(InvoiceRestockTracking.UnknownCaption)&&
                !reportSent.Edits.Single().Text.Contains(InvoiceRestockTracking.Caption),
                "pending inventory publication shows blue with unknown item status and keeps the refresh retryable");
            check(!(await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[oldInvoice.OrderId])).Contains(oldInvoice.Id),
                "showing the unknown cancellation status never becomes false evidence of inventory publication");
            reportSent.Edits.Clear();
            check(await publication.Bind(restored.Offers.Single(),new InventoryOffersController.Receipt(6003),default)is NoContentResult,"successful stock publication binds its actual channel message before report refresh");
            var debtBeforeReport=await Debt();
            typeof(TelegramWebhookController).GetField("_options",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(reportController,new TelegramOptions{NewPaymentsChatId="-1007770002"});
            check(await Rejected(()=>RefreshStockReport(reportJob))&&reportSent.Edits.Count==0,"restock refresh refuses a report bound to another configured group");
            typeof(TelegramWebhookController).GetField("_options",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(reportController,new TelegramOptions{NewPaymentsChatId="-1007770001"});
            await RefreshStockReport(reportJob);
            var blueReport=reportSent.Edits.Single();
            check(blueReport.Chat=="-1007770001"&&blueReport.Message==5555&&blueReport.Text.Contains("🔵 1. @"+marker)&&blueReport.Text.Contains("\n   "+InvoiceRestockTracking.Caption)&&blueReport.Text.Contains(oldInvoice.InvoiceNumber),
                "return-to-stock edits the exact original payments report with a blue customer row and the stock-channel note underneath");
            check((await reportService.GetPaymentTrackingReportsAsync(oldBatch.Id)).Single().Message.Contains("🔵 1. @"+marker)&&
                (await reportService.GetPaymentTrackingReportsAsync(oldBatch.Id)).Single().Actions.Count==0,"later payment-report regeneration preserves archived returned items and blue status without offering payment actions");
            reportSent.FailEdit=true;check(await Rejected(()=>RefreshStockReport(reportJob)),"failed report edit is retryable rather than silently completing the durable job");reportSent.FailEdit=false;
            await RefreshStockReport(reportJob);check(reportSent.Edits.All(e=>e.Chat=="-1007770001"&&e.Message==5555),"report retry edits the same bound message instead of posting duplicate reports");
            await db.Invoices.Where(i=>i.Id==returned.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.TelegramPaymentTrackingChatId,"-1007770001").SetProperty(i=>i.TelegramPaymentTrackingMessageId,5556L));
            await RefreshStockReport(await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==returned.OrderId&&n.Channel==InvoiceRestockTracking.Channel));
            check(reportSent.Edits.Last().Message==5556&&reportSent.Edits.Last().Text.Contains("مشتری: 🔵 @"+marker)&&reportSent.Edits.Last().Text.Contains(InvoiceRestockTracking.Caption),"standalone manual invoice payment reports receive the same blue stock-return status");
            await db.Invoices.Where(i=>i.Id==plain.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.TelegramPaymentTrackingChatId,"-1007770001").SetProperty(i=>i.TelegramPaymentTrackingMessageId,5557L));
            await RefreshStockReport(await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==plain.OrderId&&n.Channel==InvoiceRestockTracking.Channel));
            check(reportSent.Edits.Last().Message==5557&&reportSent.Edits.Last().Text.Contains("مشتری: 🔵 @"+marker)&&
                reportSent.Edits.Last().Text.Contains(InvoiceRestockTracking.UnknownCaption)&&!reportSent.Edits.Last().Text.Contains(InvoiceRestockTracking.Caption)&&
                reportSent.Edits.Last().Text.Contains("میل"),"plain invoice deletion keeps archived items visible with blue and unknown status in the original manual payments message");
            var plainBatch=new InvoiceIssuanceBatch{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,CreatedByTelegramUserId=owner.ToString()};
            db.InvoiceIssuanceBatches.Add(plainBatch);db.InvoiceIssuanceBatchSalesLists.Add(new(){InvoiceIssuanceBatchId=plainBatch.Id,SalesListId=source.Id});await db.SaveChangesAsync();
            await db.Orders.Where(o=>o.Id==plain.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.InvoiceIssuanceBatchId,plainBatch.Id));
            await reportService.SetPaymentTrackingMessageAsync(plainBatch.Id,source.Id,"-1007770001",5558,default);
            await RefreshStockReport(await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.OrderId==plain.OrderId&&n.Channel==InvoiceRestockTracking.Channel));
            var plainReport=(await reportService.GetPaymentTrackingReportsAsync(plainBatch.Id)).Single();
            check(reportSent.Edits.Last().Message==5558&&plainReport.Message.Contains("🔵 1. @"+marker)&&
                plainReport.Message.Contains("\n   "+InvoiceRestockTracking.UnknownCaption)&&plainReport.Actions.Count==0,
                "plain deletion remains visible with blue and unknown status after batch report regeneration without payment actions");
            check(await Debt()==debtBeforeReport&&await db.Invoices.AsNoTracking().AnyAsync(i=>i.Id==oldInvoice.Id&&i.Status==InvoiceStatus.Cancelled),"blue report updates and retries never change invoice cancellation or the customer's financial balance");
            await db.NotificationOutbox.Where(n=>n.Id==restored.Offers.Single()).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            await db.SalesLists.Where(l=>l.Id==restored.Offers.Single()).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramMessageId,(long?)null));
            check((await reportService.GetPaymentTrackingReportsAsync(oldBatch.Id)).Single().Message.Contains("🔵 1. @"+marker),"removing an inventory post after its later sale cannot erase the original invoice's durable blue transfer status");
            await db.NotificationOutbox.Where(n=>n.Id==reportJob.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            await db.NotificationOutbox.Where(n=>n.Id==restored.Offers.Single()).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Failed));
            check((await InvoiceRestockTracking.PublishedInvoiceIdsAsync(db,[oldInvoice.OrderId])).Contains(oldInvoice.Id),"a completed verified report preserves blue status even if its old publication job is later reviewed");
            var returnedOffer=await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==restored.Offers.Single());
            var buyer=marker+"rebuyer";var buy=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=returnedOffer.Id,TelegramUserId=buyer,TelegramUsername=buyer,VolumeMl=1.5m,BottleId=bottle.Id,BottlePrice=5000,PerfumePricePerMl=returnedOffer.PricePerMl,ExpiresAt=DateTime.UtcNow.AddDays(1),CreatedAt=DateTime.UtcNow,ExternalReference=marker+Guid.NewGuid().ToString("N")};
            db.SalesListRequests.Add(buy);await db.SaveChangesAsync();await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(buy.Id,buyer);db.ChangeTracker.Clear();
            var billing=DispatchProxy.Create<ISender,ReadyBillingSender>();((ReadyBillingSender)(object)billing).Db=db;
            var billed=await new InvoiceIssuanceService(db,billing).IssueCompletedListsAsync([returnedOffer.Id],owner.ToString());
            var nextOrder=await db.OrderItems.AsNoTracking().Where(x=>x.SalesListId==returnedOffer.Id).Select(x=>x.OrderId).SingleAsync();orders.Add(nextOrder);
            check(billed.InvoiceNumbers.Count==1&&billed.InvoiceNumbers.Single()!=oldInvoice.InvoiceNumber&&await db.Invoices.AnyAsync(i=>i.OrderId==nextOrder&&i.Status==InvoiceStatus.Issued),"returned inventory can be purchased and invoiced again without reusing its cancelled invoice");
            var missing=await Invoice(false,1m);await db.SalesLists.Where(l=>l.Id==source.Id).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramPhotoFileId,(string?)null));var missingDebt=await Debt();
            check(await Rejected(async()=>{await service.ResolveAsync(Caption(missing),true);})&&await Debt()==missingDebt&&await db.Invoices.AnyAsync(i=>i.Id==missing.Id&&i.Status==InvoiceStatus.Issued),"missing product photo prevents direct return without cancelling the invoice or changing debt");
            await db.SalesLists.Where(l=>l.Id==source.Id).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.TelegramPhotoFileId,"fixture-photo"));

            var ui=await Invoice(false,4m);var sender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var sent=(MarketingSender)(object)sender;
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var f in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))if(f.Name.EndsWith("Drafts")&&f.FieldType.GetConstructor(Type.EmptyTypes)is not null)f.SetValue(controller,Activator.CreateInstance(f.FieldType));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),AdminChatId="-100123",ReadyInventoryChannelId="@zibasheperfume_mojodi"}),("_adminActivityContext",new AdminActivityContext())})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
            const string prefix="invoiceadmin:cancelinvoice:";
            Task Select(string action,long user=owner,string type="private")=>(Task)typeof(TelegramWebhookController).GetMethod("HandleInvoiceCancellationCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramCallbackQuery("fixture",new(user,null),new TelegramMessage(null,new(user,type),new(user,null),null,MessageId:1),prefix+action),CancellationToken.None])!;
            Task<bool> Input(string caption,long user=owner,bool photo=false)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleInvoiceCancellationMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramMessage(photo?null:caption,new(user,"private"),new(user,null),null,Caption:photo?caption:null,MessageId:2),CancellationToken.None])!;
            await Select("start",owner+1);check(sent.Texts.Last().Contains("فقط برای ادمین"),"customers cannot access invoice cancellation callbacks");
            await Select("start",owner,"supergroup");check(sent.Texts.Last().Contains("خصوصی"),"financial cancellation cannot be initiated in a group");
            await Select("start");check(sent.Texts.Last().Contains("کپشن کامل"),"plain cancellation explicitly requests the invoice caption");
            await Input(Caption(ui),photo:true);check(sent.Buttons.Any(s=>s.StartsWith(prefix+"confirm:"))&&await db.Invoices.AnyAsync(i=>i.Id==ui.Id&&i.Status==InvoiceStatus.Issued),"forwarded photo/PDF caption produces an exact cancellation preview before any financial write");
            var oldConfirm=sent.Buttons.Last(s=>s.StartsWith(prefix+"confirm:"));await Select("cancel");await Select(oldConfirm[prefix.Length..]);
            check(await db.Invoices.AnyAsync(i=>i.Id==ui.Id&&i.Status==InvoiceStatus.Issued),"cancelled or expired approval cannot cancel an invoice later");
            sent.AllowAccountingAdmin=true;await Select("return-start",owner+1);check(sent.Texts.Last().Contains("کپشن کامل"),"authorized accountant can start the separate return-to-stock caption workflow");
            await Input(Caption(ui),owner+1);check(sent.Texts.Last().Contains("قیمت روز هر میل")&&sent.Texts.Last().Contains("قیمت هر میل در فاکتور"),"accountant sees original invoice and current per-ml prices before direct stock return");
            await Select("edit-price:0",owner+1);await Input("۸۵۰۰",owner+1);check(sent.Texts.Last().Contains("8,500")&&sent.Texts.Last().Contains("39,000"),"accountant can edit Persian-digit per-ml price and preview exact perfume-plus-bottle total");
            await Select("accept-price:0",owner+1);check(sent.Buttons.Last(s=>s.StartsWith(prefix+"confirm:")).Length<=64,"final transfer approval fits Telegram callback limits and remains separate from price editing");
        }
        finally
        {
            db.ChangeTracker.Clear();var items=await db.OrderItems.Where(x=>orders.Contains(x.OrderId)).Select(x=>x.Id).ToArrayAsync();var offers=(await db.SalesLists.Where(l=>l.SourceOrderItemId!=null&&items.Contains(l.SourceOrderItemId.Value)).Select(l=>l.Id).ToArrayAsync()).Concat(extraLists).ToArray();
            var batches=await db.InvoiceIssuanceBatchSalesLists.Where(l=>offers.Contains(l.SalesListId)||l.SalesListId==source.Id).Select(l=>l.InvoiceIssuanceBatchId).ToArrayAsync();
            var offerNames=offers.Select(id=>id.ToString()).ToArray();
            await db.NotificationOutbox.Where(n=>(n.OrderId!=null&&orders.Contains(n.OrderId.Value))||offers.Contains(n.Id)||offerNames.Contains(n.Recipient)||n.Payload.Contains(marker)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatchSalesLists.Where(l=>offers.Contains(l.SalesListId)||l.SalesListId==source.Id).ExecuteDeleteAsync();
            await db.InventoryPositionChanges.Where(p=>offers.Contains(p.SalesListId)).ExecuteDeleteAsync();
            var req=await db.OrderItems.Where(x=>orders.Contains(x.OrderId)&&x.SourceSalesListRequestId!=null).Select(x=>x.SourceSalesListRequestId!.Value).ToArrayAsync();
            await db.Payments.Where(p=>orders.Contains(p.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(x=>orders.Contains(x.OrderId)).ExecuteDeleteAsync();await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>req.Contains(r.Id)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>offers.Contains(l.Id)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>l.Id==source.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id||c.Username!=null&&c.Username.StartsWith(marker)).ExecuteDeleteAsync();await db.Bottles.Where(b=>b.Id==bottle.Id).ExecuteDeleteAsync();
        }
    }
}

public class RestockTrackingSender:DispatchProxy
{
    public List<(string Chat,long Message,string Text)> Edits {get;}=[];
    public bool FailEdit {get;set;}
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name!="EditTextWithKeyboardAsync")throw new NotSupportedException("Restock reports must only edit their bound message: "+method.Name);
        Edits.Add(((string)args![0]!, (long)args[1]!, (string)args[2]!));
        return Task.FromResult(new TelegramSendResult(!FailEdit,FailEdit?"fixture network failure":null));
    }
}
