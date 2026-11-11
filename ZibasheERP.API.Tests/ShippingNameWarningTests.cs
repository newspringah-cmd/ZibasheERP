using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class ShippingNameWarningTests
{
    public static async Task Run(Action<bool,string> check)
    {
        check(ShippingRecipientNameSimilarity.Similar("نَرگس اَمـيني","نرگس امینی"),"recipient warning compares canonical Persian letters, diacritics and tatweel");
        check(ShippingRecipientNameSimilarity.Similar("لیلا بابازاده","ليلا بابا زاده"),"spacing and joined surname variants trigger the same name warning");
        check(ShippingRecipientNameSimilarity.Similar("بیکی","بیگی"),"a close short postal surname triggers a manual warning without becoming an automatic tracking match");
        check(!ShippingRecipientNameSimilarity.Similar("مریم رحیمی","مریم محمدی")&&!ShippingRecipientNameSimilarity.Similar("نرگس امینی","لیلا بابازاده"),"a shared common first name alone does not warn about unrelated surnames");
        check(!ShippingRecipientNameSimilarity.Similar("@recipient","recipient")&&!ShippingRecipientNameSimilarity.Similar("۰۹۱۲۰۰۰۰۰۰۰","09120000000"),"usernames and phone numbers cannot masquerade as recipient-name collisions");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const string chat="-1004811577111";var options=new TelegramOptions{ShippingChatId=chat};var customerIds=new List<Guid>();var batches=new List<Guid>();var next=5100L;var stamp=Guid.NewGuid().ToString("N")[..12];
        var sender=DispatchProxy.Create<ITelegramMessageSender,NameWarningSender>();var sent=(NameWarningSender)(object)sender;
        async Task<(Customer Customer,Address Address,long Message)> Fixture(string name,int days=0,bool linked=true)
        {
            var customer=new Customer{Id=Guid.NewGuid(),FullName="Name warning fixture",Username="warn_"+stamp+customerIds.Count,Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow.AddDays(-days)};customerIds.Add(customer.Id);
            var address=new Address{Id=Guid.NewGuid(),CustomerId=customer.Id,ReceiverName=name,FullAddress="تهران خیابان نمونه",City="تهران",CreatedAt=DateTime.UtcNow.AddDays(-days)};
            var message=++next;db.Customers.Add(customer);db.Addresses.Add(address);
            if(linked)db.CustomerTelegramGroups.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId="-100"+Random.Shared.Next(10000000,99999999),CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow});
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=chat,Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddDays(-days),
                Payload=JsonSerializer.Serialize(new{CustomerId=customer.Id,AddressId=address.Id,RequestId=(Guid?)null,ItemIds=Array.Empty<Guid>(),ChatId=chat,Kind="root",Installed=true,MessageId=message})});await db.SaveChangesAsync();return(customer,address,message);
        }
        Task<NotificationOutbox> Warning(Guid customer)=>db.NotificationOutbox.SingleAsync(n=>n.CustomerId==customer&&n.Channel==ShippingNameWarnings.Channel);
        async Task Pending()
        {
            foreach(var job in await db.NotificationOutbox.Where(n=>n.Channel==ShippingNameWarnings.Channel&&n.Recipient==chat&&n.Status==NotificationOutboxStatus.Pending).ToArrayAsync())
            {await ShippingNameWarnings.ProcessAsync(db,sender,options,job,default);await db.SaveChangesAsync();}
        }
        try
        {
            var a=await Fixture("نرگس امینی");var b=await Fixture("نرگس اميني",70,false);var other=await Fixture("سارا احمدی");
            check((await TrackingPreparedAddresses.LoadAsync(db,chat,default)).All(r=>r.CustomerId!=b.Customer.Id),"warning-only scanning does not weaken the date and connected-group guards of automatic tracking matching");
            await ShippingNameWarnings.ReconcileAsync(db,options,default);
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel==ShippingNameWarnings.Channel&&customerIds.Contains(n.CustomerId))==2,"both open addresses with similar names receive one durable warning, including an older unlinked legacy address");
            await Pending();
            check(sent.Replies.Count==2&&sent.Replies.Any(r=>r.Source==a.Message)&&sent.Replies.Any(r=>r.Source==b.Message)&&sent.Replies.All(r=>r.Chat==chat&&r.Text.StartsWith(ShippingNameWarnings.Heading)&&r.Text.Contains("@"+a.Customer.Username)&&r.Text.Contains("@"+b.Customer.Username)),
                "warnings reply to each exact address with red crosses, both names, usernames and the other address link");
            check(sent.Edits.Count==0&&sent.Deletes.Count==0,"creating a name warning never rewrites an address body or its existing buttons");
            await ShippingNameWarnings.ReconcileAsync(db,options,default);await Pending();
            check(sent.Replies.Count==2,"repeated scans do not flood the preparation group with duplicate warnings");
            b.Address.ReceiverName="نرگس امینیان";b.Address.UpdatedAt=DateTime.UtcNow;await db.SaveChangesAsync();
            await ShippingNameWarnings.ReconcileAsync(db,options,default);await Pending();
            check(sent.Replies.Count==2&&sent.Edits.Count==2&&sent.Edits.All(e=>e.Message!=a.Message&&e.Message!=b.Message),"updated similar names edit the two existing warning messages instead of replacing addresses or sending duplicate alerts");
            await ShippingTrackingNotices.QueueAsync(db,options,new(b.Customer.Id,chat,b.Message,null),default);
            await ShippingNameWarnings.ReconcileAsync(db,options,default);await Pending();
            check(sent.Deletes.Count==2&&sent.Deletes.All(id=>id!=a.Message&&id!=b.Message)&&JsonSerializer.Deserialize<ShippingNameWarnings.Data>((await Warning(a.Customer.Id)).Payload)!.Active==false,
                "successful legacy tracking retires both name warnings and only deletes the bot's recorded warning replies");
            // A prior delivery for the same account must not close a later, different physical address.
            await ShippingTrackingNotices.QueueAsync(db,options,new(a.Customer.Id,chat,a.Message,null),default);
            var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),CreatedAt=DateTime.UtcNow};batches.Add(batch.Id);db.TrackingImportBatches.Add(batch);
            var newAddress=new Address{Id=Guid.NewGuid(),CustomerId=a.Customer.Id,ReceiverName="نرگس امینی",FullAddress="تهران خیابان جدید",CreatedAt=DateTime.UtcNow};db.Addresses.Add(newAddress);var newMessage=++next;
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=a.Customer.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=chat,Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,
                Payload=JsonSerializer.Serialize(new{CustomerId=a.Customer.Id,AddressId=newAddress.Id,RequestId=(Guid?)null,ItemIds=Array.Empty<Guid>(),ChatId=chat,Kind="root",Installed=true,MessageId=newMessage})});
            db.TrackingDispatches.Add(new(){Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=a.Customer.Id,TrackingCode=Guid.NewGuid().ToString("N"),Status=TrackingDispatchStatus.Sent,SentAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,RecipientName="Synthetic old recipient"});await db.SaveChangesAsync();
            check((await TrackingPreparedAddresses.LoadForNameWarningsAsync(db,chat,default)).Any(r=>r.MessageId==newMessage),"a prior account-level tracking delivery cannot hide a newer distinct ready address from name warnings");
            var e=await Fixture("لیلا بابازاده");var f=await Fixture("ليلا بابا زاده");await ShippingNameWarnings.ReconcileAsync(db,options,default);
            var unknown=await Warning(e.Customer.Id);sent.ReplyResult=new(false,"fixture timeout");var attempts=sent.ReplyAttempts;
            await ShippingNameWarnings.ProcessAsync(db,sender,options,unknown,default);await db.SaveChangesAsync();
            await ShippingNameWarnings.ReconcileAsync(db,options,default);await ShippingNameWarnings.ProcessAsync(db,sender,options,unknown,default);
            check(unknown.Status==NotificationOutboxStatus.Failed&&sent.ReplyAttempts==attempts+1,"an uncertain warning send is retained for review without a blind duplicate");
            var delayed=await Warning(f.Customer.Id);sent.ReplyResult=new(false,"429"){IsRetryable=true,RetryAfterSeconds=120};var start=DateTime.UtcNow;
            await ShippingNameWarnings.ProcessAsync(db,sender,options,delayed,default);await db.SaveChangesAsync();
            check(delayed.Status==NotificationOutboxStatus.Pending&&delayed.NextAttemptAt>=start.AddSeconds(120)&&JsonSerializer.Deserialize<ShippingNameWarnings.Data>(delayed.Payload)!.AttemptedAt is null,"a definite warning rate limit keeps the complete cooldown and permits safe retry");
            var cooldown=delayed.NextAttemptAt;await ShippingNameWarnings.ReconcileAsync(db,options,default);
            check(delayed.NextAttemptAt==cooldown,"unchanged collision scans cannot erase a Telegram cooldown");
            sent.ReplyResult=null;delayed.NextAttemptAt=DateTime.UtcNow;await ShippingNameWarnings.ProcessAsync(db,sender,options,delayed,default);await db.SaveChangesAsync();
            check(delayed.Status==NotificationOutboxStatus.Processed&&JsonSerializer.Deserialize<ShippingNameWarnings.Data>(delayed.Payload)!.MessageId>0,"warning delivery resumes safely after the definite retry window");
            var foreign=new NotificationOutbox{Id=delayed.Id,CustomerId=delayed.CustomerId,Channel=ShippingNameWarnings.Channel,Recipient="-1001",Payload=delayed.Payload};attempts=sent.ReplyAttempts;
            await ShippingNameWarnings.ProcessAsync(db,sender,options,foreign,default);
            check(foreign.Status==NotificationOutboxStatus.Failed&&sent.ReplyAttempts==attempts,"a warning cannot be moved to a different group by altering queued metadata");
            check(!await db.OrderItems.AnyAsync(i=>customerIds.Contains(i.Order!.CustomerId))&&!await db.Invoices.AnyAsync(i=>customerIds.Contains(i.Order!.CustomerId)),"name warnings create no invoice, order item or fulfillment changes");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>customerIds.Contains(n.CustomerId)).ExecuteDeleteAsync();await db.TrackingDispatchDeliveries.Where(d=>batches.Contains(d.TrackingDispatch!.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>batches.Contains(d.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingImportBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>customerIds.Contains(g.CustomerId)).ExecuteDeleteAsync();await db.Addresses.Where(a=>customerIds.Contains(a.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>customerIds.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
public class NameWarningSender:DispatchProxy
{
    public List<(string Chat,long Source,string Text)> Replies=[];public List<(long Message,string Text)> Edits=[];public List<long> Deletes=[];public TelegramSendResult? ReplyResult;public int ReplyAttempts;private long next=1000000;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="SendReplyOnceAsync")
        {ReplyAttempts++;var result=ReplyResult??new TelegramSendResult(true,MessageId:++next);if(result.IsSuccessful)Replies.Add(((string)args![0]!, (long)args[2]!, (string)args[1]!));return Task.FromResult(result);}
        if(method.Name=="EditTextWithKeyboardAsync"){Edits.Add(((long)args![1]!, (string)args[2]!));return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name=="DeleteMessageAsync"){Deletes.Add((long)args![1]!);return Task.FromResult(new TelegramSendResult(true));}
        throw new InvalidOperationException("Name warnings may only send, edit or remove their own scoped warning replies");
    }
}
