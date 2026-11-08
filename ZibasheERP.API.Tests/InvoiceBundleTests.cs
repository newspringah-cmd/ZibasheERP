using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class InvoiceBundleTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=771234567;const string chat="-10088776655";
        var customer=new Customer{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,FullName="Bundle fixture",Mobile=Guid.NewGuid().ToString("N")[..18]};db.Customers.Add(customer);
        var orders=new[]{0,1}.Select(_=>new Order{Id=Guid.NewGuid(),CustomerId=customer.Id,OrderNumber="BUNDLE-"+Guid.NewGuid().ToString("N"),CreatedAt=DateTime.UtcNow,Status=OrderStatus.Invoiced}).ToArray();db.Orders.AddRange(orders);
        var invoices=orders.Select(o=>new Invoice{Id=Guid.NewGuid(),OrderId=o.Id,InvoiceNumber="INV-BUNDLE-"+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Issued,CreatedAt=DateTime.UtcNow,IssuedAt=DateTime.UtcNow}).ToArray();db.Invoices.AddRange(invoices);
        invoices[0].TelegramInvoiceChatId=chat;invoices[0].TelegramInvoiceMessageId=104;
        var group=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chat,Title="Bundle fixture",IsActive=true,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};db.CustomerTelegramGroups.Add(group);await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,BundleSender>();var sent=(BundleSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),BotToken="123:fixture"}),("_adminActivityContext",new AdminActivityContext())})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        Task Process(NotificationOutbox job)=>(Task)typeof(TelegramWebhookController).GetMethod("ProcessInvoiceCancellationCleanupAsync",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(controller,[job,CancellationToken.None])!;
        async Task<bool> Rejected(Func<Task> action){try{await action();return false;}catch(InvalidOperationException){return true;}}
        Task<bool> Legacy(TelegramMessage message)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleInvoiceCleanupReplyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[message,CancellationToken.None])!;
        var jobs=new List<Guid>();
        try
        {
            for(var id=101;id<=104;id++){await InvoiceMessageLifecycle.RecordAsync(db,orders[0].Id,Guid.NewGuid(),chat,id,new[]{"sticker","photo","text","pdf"}[id-101]);await db.SaveChangesAsync();}
            await InvoiceMessageLifecycle.RecordAsync(db,orders[0].Id,Guid.NewGuid(),chat,101,"sticker");await db.SaveChangesAsync();
            check(await db.NotificationOutbox.CountAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceMessageReceipt")==4,"invoice greeting, photo, text and PDF have exact persistent receipts without duplicated message identity");
            check(await Rejected(async()=>await InvoiceMessageLifecycle.QueueKnownAsync(db,invoices[0])),"active invoice cannot queue deletion of its message bundle");
            invoices[0].Status=InvoiceStatus.Cancelled;orders[0].Status=OrderStatus.Cancelled;await db.SaveChangesAsync();
            await InvoiceMessageLifecycle.QueueKnownAsync(db,invoices[0]);await db.SaveChangesAsync();await InvoiceMessageLifecycle.QueueKnownAsync(db,invoices[0]);await db.SaveChangesAsync();
            var cleanup=await db.NotificationOutbox.Where(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup").ToArrayAsync();
            check(cleanup.Length==4,"cancellation queues every recorded invoice message once and deduplicates the PDF pointer");
            foreach(var row in cleanup)await Process(row);
            check(sent.Deletes.Select(d=>d.Message).Order().SequenceEqual(new long[]{101,102,103,104})&&sent.Deletes.All(d=>d.Chat==chat),"bundle cleanup deletes only the four exact messages associated with the cancelled invoice");
            foreach(var row in cleanup)await Process(row);
            check(sent.Deletes.Count==4,"replayed cleanup skips already deleted receipts");
            await InvoiceMessageLifecycle.RecordAsync(db,orders[0].Id,Guid.NewGuid(),chat,105,"late-pdf");await db.SaveChangesAsync();
            check(await db.NotificationOutbox.CountAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup")==5,"a document delivered after cancellation automatically joins cleanup rather than restoring the invoice");
            await InvoiceMessageLifecycle.RecordAsync(db,orders[1].Id,Guid.NewGuid(),chat,106,"photo");await db.SaveChangesAsync();
            var other=await db.NotificationOutbox.SingleAsync(n=>n.OrderId==orders[1].Id&&n.Channel=="InvoiceMessageReceipt");
            var forged=new NotificationOutbox{OrderId=orders[0].Id,Channel="InvoiceCancellationCleanup",Recipient=chat,Payload=JsonSerializer.Serialize(new InvoiceMessageLifecycle.Cleanup(invoices[0].Id,chat,106,other.Id))};
            check(await Rejected(()=>Process(forged))&&sent.Deletes.Count==4,"cleanup cannot delete a different invoice's message even in the same group");
            var late=await db.NotificationOutbox.SingleAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup"&&n.Payload.Contains("105"));sent.DeleteResult=new(false,"Bad Request: message to delete not found");
            await Process(late);check(sent.Deletes.Last().Message==105,"already missing Telegram message is treated as successful idempotent cleanup");sent.DeleteResult=new(true);
            var source=new NotificationOutbox{Id=Guid.NewGuid(),OrderId=orders[0].Id,Channel="Telegram",EventType="InvoicePerfumePhoto",Recipient=chat,Payload="{}",Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow};db.NotificationOutbox.Add(source);await db.SaveChangesAsync();
            TelegramMessage Reply(long botId,bool bot=true,long? at=null)=>new("/invoicecleanup "+invoices[0].InvoiceNumber,new(long.Parse(chat),"supergroup"),new(owner,null),null,MessageId:400,
                ReplyToMessage:new TelegramMessage(null,new(long.Parse(chat),"supergroup"),new(botId,null,IsBot:bot),null,Photo:[new("fixture",100,100)],MessageId:300,Date:at??DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var before=await db.NotificationOutbox.CountAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup");
            await Legacy(Reply(123,false));await Legacy(Reply(999));await Legacy(Reply(123,at:DateTimeOffset.UtcNow.AddHours(-4).ToUnixTimeSeconds()));
            check(await db.NotificationOutbox.CountAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup")==before,"legacy cleanup rejects human messages, other bots and messages outside the invoice delivery time window");
            await Legacy(Reply(123));
            check(await db.NotificationOutbox.CountAsync(n=>n.OrderId==orders[0].Id&&n.Channel=="InvoiceCancellationCleanup")==before+1,"authorized accountant can select an untracked old invoice photo by replying to its exact message");
            var regular=new NotificationOutbox{Id=Guid.NewGuid(),Channel="MarketingPromotion",EventType="ReadyInventoryInvoiceReport",CreatedAt=new(1900,1,1),Payload="{}"};
            var publication=new NotificationOutbox{Id=Guid.NewGuid(),Channel="MarketingPromotion",EventType="ReadyInventoryPublication",CreatedAt=new(1900,1,2),Payload="{}"};db.NotificationOutbox.AddRange(regular,publication);jobs.AddRange([regular.Id,publication.Id]);await db.SaveChangesAsync();
            var api=new MarketingPromotionsController(new NotificationOutboxRepository(db),sender,db);
            var inventoryResult=(OkObjectResult)await api.Pending(default,"inventory");
            var inventoryRows=JsonSerializer.SerializeToElement(inventoryResult.Value);
            check(inventoryRows[0].GetProperty("Id").GetGuid()==publication.Id&&await db.NotificationOutbox.AnyAsync(n=>n.Id==regular.Id&&n.Status==NotificationOutboxStatus.Pending),"inventory publication claims its own lane without waiting behind older payment reports");
            var regularResult=(OkObjectResult)await api.Pending(default,"regular");
            check(JsonSerializer.SerializeToElement(regularResult.Value)[0].GetProperty("Id").GetGuid()==regular.Id,"regular dispatcher cannot claim an inventory publication job");
            check(await api.Pending(default,"invalid")is BadRequestResult,"unknown dispatcher lane cannot claim a queue row");
        }
        finally
        {
            var ids=orders.Select(o=>o.Id).ToArray();db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>n.OrderId!=null&&ids.Contains(n.OrderId.Value)||jobs.Contains(n.Id)).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>ids.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>ids.Contains(o.Id)).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>g.Id==group.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id).ExecuteDeleteAsync();
        }
    }
}
public class BundleSender:DispatchProxy
{
    public List<(string Chat,long Message)> Deletes=[];public TelegramSendResult DeleteResult=new(true);
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="DeleteMessageAsync"){Deletes.Add(((string)args![0]!, (long)args[1]!));return Task.FromResult(DeleteResult);}
        if(method.Name is "SendAsync" or "SendInlineKeyboardAsync")return Task.FromResult(new TelegramSendResult(true));
        if(method.Name=="IsChatAdministratorAsync")return Task.FromResult(false);
        throw new NotSupportedException(method.Name);
    }
}
