using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class PaidPaymentTrackingCleanupTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var cs=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var guard=new SqlConnectionStringBuilder(cs);if(guard.InitialCatalog!="ZibasheERPCloudDev"||guard.DataSource!="127.0.0.1,14333")throw new Exception("Wrong database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        var perfume=await db.Perfumes.Select(x=>x.Id).FirstAsync();var customer=await db.Customers.Select(x=>x.Id).FirstAsync();var now=DateTime.UtcNow;const string chat="-100999000111";
        var batch=new InvoiceIssuanceBatch{Id=Guid.NewGuid(),Status=InvoiceIssuanceBatchStatus.Issued,IssuedAt=now,CreatedAt=now};
        var lists=Enumerable.Range(0,5).Select(i=>new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(10000000,20000000),PerfumeId=perfume,EnglishName="Synthetic paid report",TelegramPhotoFileId="paid-cleanup-photo",TotalVolume=100,PricePerMl=1000,Status=SalesListStatus.Invoiced,CreatedAt=now}).ToArray();
        var links=lists.Select((l,i)=>new InvoiceIssuanceBatchSalesList{InvoiceIssuanceBatch=batch,SalesList=l,SalesListId=l.Id,InvoiceIssuanceBatchId=batch.Id,TelegramPaymentTrackingChatId=chat,TelegramPaymentTrackingPhotoMessageId=100+i*2,TelegramPaymentTrackingMessageId=101+i*2}).ToArray();
        var orders=lists.Take(4).Select(l=>new Order{Id=Guid.NewGuid(),CustomerId=customer,InvoiceIssuanceBatch=batch,InvoiceIssuanceBatchId=batch.Id,OrderNumber="cleanup-"+Guid.NewGuid().ToString("N"),Status=OrderStatus.Shipped,CreatedAt=now}).ToArray();
        var items=orders.Select((o,i)=>new OrderItem{Id=Guid.NewGuid(),Order=o,OrderId=o.Id,SalesList=lists[i],SalesListId=lists[i].Id,PerfumeId=perfume,RequestedVolumeMl=5,Quantity=1,PerfumeAmount=5000,LineTotal=5000,CreatedAt=now}).ToArray();
        var invoices=orders.Take(3).Select((o,i)=>new Invoice{Id=Guid.NewGuid(),Order=o,OrderId=o.Id,InvoiceNumber="CLEAN-"+Guid.NewGuid().ToString("N"),Status=i==0?InvoiceStatus.Paid:i==1?InvoiceStatus.Issued:InvoiceStatus.Cancelled,TotalAmount=5000,CreatedAt=now}).ToArray();
        var sender=DispatchProxy.Create<ITelegramMessageSender,PaidCleanupSender>();var proxy=(PaidCleanupSender)(object)sender;var service=new PaidPaymentTrackingCleanup(db,sender);
        try
        {
            db.SalesLists.AddRange(lists);db.InvoiceIssuanceBatches.Add(batch);db.InvoiceIssuanceBatchSalesLists.AddRange(links);db.Orders.AddRange(orders);db.OrderItems.AddRange(items);db.Invoices.AddRange(invoices);await db.SaveChangesAsync();
            var targets=(await service.CandidatesAsync(chat,default)).Where(t=>t.BatchId==batch.Id).ToArray();
            check(targets.Length==1&&targets[0].SalesListId==lists[0].Id,"only paid invoices qualify; issued, cancelled, missing-invoice and empty lists stay visible");
            check(!(await service.CandidatesAsync("-100555",default)).Any(t=>t.BatchId==batch.Id),"cleanup is confined to the configured new-payments destination");
            var report=(await new InvoiceIssuanceService(db,null!).GetPaymentTrackingReportsAsync(batch.Id)).Single(r=>r.SalesListId==lists[0].Id);
            check(report.AllPaid,"paid-list report explicitly exposes completion while fulfillment may already be shipped");
            invoices[0].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();
            check(!await service.ProcessAsync(targets[0],chat,default)&&proxy.Deleted.Count==0,"a payment reversal after candidate selection prevents Telegram deletion");
            invoices[0].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();
            check(await service.ProcessAsync(targets[0],chat,default)&&proxy.Deleted.SequenceEqual(new long[]{100,101}),"all-paid cleanup deletes both the perfume photo and report text");
            var receipt=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==PaidPaymentTrackingCleanup.Key(targets[0]));
            check(receipt.Status==NotificationOutboxStatus.Processed&&receipt.Payload.Contains("101")&&links[0].TelegramPaymentTrackingMessageId==null&&links[0].TelegramPaymentTrackingPhotoMessageId==null,"deleted post identifiers survive in the durable receipt while live links are cleared");
            check(!await service.ProcessAsync(targets[0],chat,default)&&proxy.Deleted.Count==2,"repeated cleanup does not delete unrelated messages or duplicate a completed operation");
            check(await db.Invoices.CountAsync(i=>invoices.Select(v=>v.Id).Contains(i.Id))==3&&await db.OrderItems.CountAsync(i=>i.SalesListId==lists[0].Id)==1&&invoices[0].Status==InvoiceStatus.Paid,"cleanup preserves invoices, order items and financial states");
            invoices[0].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var pair in new(string,object)[]{("_db",db),("_sender",sender),("_invoiceIssuanceService",new InvoiceIssuanceService(db,null!)),("_options",new TelegramOptions{NewPaymentsChatId=chat})})
                typeof(TelegramWebhookController).GetField(pair.Item1,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,pair.Item2);
            await (Task)typeof(TelegramWebhookController).GetMethod("RefreshPaymentTrackingReportAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,new object[]{batch.Id,-999L,CancellationToken.None})!;
            check(links[0].TelegramPaymentTrackingPhotoMessageId==200&&links[0].TelegramPaymentTrackingMessageId==201,
                "reversing payment after cleanup republishes the unpaid report and photo through the existing financial refresh flow");
            invoices[0].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();
            var retry=(await service.CandidatesAsync(chat,default)).Single(t=>t.SalesListId==lists[0].Id);proxy.FailedMessage=200;
            check(!await service.ProcessAsync(retry,chat,default)&&links[0].TelegramPaymentTrackingMessageId==201,"a Telegram deletion restriction retains the report link instead of claiming success");
            var failed=await db.NotificationOutbox.SingleAsync(n=>n.Id==PaidPaymentTrackingCleanup.Key(retry));
            check(failed.Status==NotificationOutboxStatus.Failed&&failed.NextAttemptAt>DateTime.UtcNow&&receipt.Id!=failed.Id,"restricted posts retain a deferred manual-review record and a new post has an independent cleanup identity");
            proxy.FailedMessage=null;failed.NextAttemptAt=DateTime.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
            check(await service.ProcessAsync(retry,chat,default)&&await db.NotificationOutbox.CountAsync(n=>n.Channel==PaidPaymentTrackingCleanup.Channel&&n.Payload.Contains(batch.Id.ToString()))==2,"a retried recreated report is cleaned without erasing the previous audit receipt");
            invoices[1].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();var next=(await service.CandidatesAsync(chat,default)).Single(t=>t.SalesListId==lists[1].Id);proxy.MissingMessage=103;
            check(await service.ProcessAsync(next,chat,default),"a message already manually deleted counts as acknowledged cleanup");
            var before=proxy.Deleted.Count;var refused=false;try{await service.ProcessAsync(next,"-100555",default);}catch(InvalidOperationException){refused=true;}
            check(refused&&proxy.Deleted.Count==before,"a mismatched destination cannot trigger any delete call");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>n.Channel==PaidPaymentTrackingCleanup.Channel&&n.Payload.Contains(batch.Id.ToString())).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>orders.Select(o=>o.Id).Contains(i.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(i=>orders.Select(o=>o.Id).Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>o.InvoiceIssuanceBatchId==batch.Id).ExecuteDeleteAsync();await db.InvoiceIssuanceBatchSalesLists.Where(l=>l.InvoiceIssuanceBatchId==batch.Id).ExecuteDeleteAsync();await db.InvoiceIssuanceBatches.Where(b=>b.Id==batch.Id).ExecuteDeleteAsync();await db.SalesLists.Where(l=>lists.Select(x=>x.Id).Contains(l.Id)).ExecuteDeleteAsync();
        }
    }
}
public class PaidCleanupSender:DispatchProxy
{
    public List<long> Deleted=new();public long? FailedMessage;public long? MissingMessage;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="SendPhotoHtmlAsync")return Task.FromResult(new TelegramSendResult(true,MessageId:200));
        if(method.Name=="SendInlineKeyboardAsync")return Task.FromResult(new TelegramSendResult(true,MessageId:201));
        if(method.Name=="EditTextWithKeyboardAsync")return Task.FromResult(new TelegramSendResult(true));
        if(method!.Name!="DeleteMessageAsync")throw new Exception("Unexpected sender action");
        var id=(long)args![1]!;Deleted.Add(id);return Task.FromResult(id==FailedMessage?new TelegramSendResult(false,"Bad Request: message can't be deleted"):id==MissingMessage?new TelegramSendResult(false,"Bad Request: message to delete not found"):new TelegramSendResult(true));
    }
}
