using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class ShippingTrackingNoticeTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new Exception("Isolated SQL required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const string chat="-10088122999";var opts=new TelegramOptions{ShippingChatId=chat,OwnerUserId="741471928"};
        var sender=DispatchProxy.Create<ITelegramMessageSender,TrackingNoticeSender>();var transport=(TrackingNoticeSender)(object)sender;
        var customers=new List<Guid>();var batches=new List<Guid>();var next=21000L;
        async Task<(Customer C,long Source,TrackingDispatch D)> Fixture(bool root=true,bool old=false)
        {
            var c=new Customer{Id=Guid.NewGuid(),Mobile=Guid.NewGuid().ToString("N")[..18],FullName="Synthetic manual tracking",CreatedAt=DateTime.UtcNow};
            c.Notes="manual-address-registration:1907823600:"+c.Id.ToString("N");customers.Add(c.Id);db.Customers.Add(c);
            var source=++next;var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),CreatedAt=DateTime.UtcNow};batches.Add(batch.Id);db.TrackingImportBatches.Add(batch);
            var d=new TrackingDispatch{Id=Guid.NewGuid(),CustomerId=c.Id,ImportBatchId=batch.Id,RecipientName="Synthetic",TrackingCode=Guid.NewGuid().ToString("N"),CardImage=[1],Status=TrackingDispatchStatus.Ready,CreatedAt=DateTime.UtcNow};db.TrackingDispatches.Add(d);
            if(root)db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=c.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=chat,Status=NotificationOutboxStatus.Processed,CreatedAt=old?batch.CreatedAt.AddDays(-60):batch.CreatedAt.AddSeconds(-1),
                Payload=JsonSerializer.Serialize(new ShippingTrackingNotices.Source(c.Id,null,[],chat,"root",source,true))});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();return(c,source,d);
        }
        async Task<NotificationOutbox> Notice(Guid c)=>await db.NotificationOutbox.SingleAsync(n=>n.Channel==ShippingTrackingNotices.Channel&&n.CustomerId==c);
        async Task Process(TrackingDispatch d)
        {
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",opts),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,value);
            await (Task)typeof(TelegramWebhookController).GetMethod("ProcessTrackingBatchAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[1L,d.ImportBatchId,CancellationToken.None,d.Id,true])!;
        }
        Task Mark(NotificationOutbox job,ShippingTrackingNotices.Data data,CancellationToken ct)
        {
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",opts)})typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,value);
            return (Task)typeof(TelegramWebhookController).GetMethod("MarkTrackingAddressButtonAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[job,data,ct])!;
        }
        try
        {
            var good=await Fixture();await Process(good.D);db.ChangeTracker.Clear();var job=await Notice(good.C.Id);
            check(transport.Photos==1&&job.Status==NotificationOutboxStatus.Pending&&transport.Replies==0,"accepted PDF tracking queues a separate address reply without delaying or resending the customer's code");
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default,(data,ct)=>Mark(job,data,ct));await db.SaveChangesAsync();
            check(transport.Buttons.SelectMany(r=>r).Any(b=>b.Text==ShippingTrackingNotices.Text&&b.Style=="success"&&b.CallbackData=="shipping:trackingdone")&&transport.Buttons.SelectMany(r=>r).Count(b=>b.CallbackData?.StartsWith("shipping:refs:")==true)==4,"successful tracking marks its original address button green and preserves all four reference actions");
            check(job.Status==NotificationOutboxStatus.Processed&&transport.LastChat==chat&&transport.LastReplyTo==good.Source&&transport.LastText==ShippingTrackingNotices.Text,"successful notice is threaded under the exact preparation address with the requested green text");
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);
            await ShippingTrackingNotices.QueueAsync(db,opts,new(good.C.Id,chat,good.Source,null),default);
            check(transport.Replies==1&&await db.NotificationOutbox.CountAsync(n=>n.CustomerId==good.C.Id&&n.Channel==ShippingTrackingNotices.Channel)==1,"replay and repeated completion keep one address notice and never resend tracking");
            var oldEdits=transport.Edits;var oldReplies=transport.Replies;
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default,(data,ct)=>Mark(job,data,ct));
            check(transport.Edits==oldEdits&&transport.Replies==oldReplies,"a replayed successful notice neither resends tracking or replies nor rewrites an already updated button");
            job.Payload=JsonSerializer.Serialize(JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(job.Payload)! with{ButtonUpdated=false});await db.SaveChangesAsync();
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default,(data,ct)=>Mark(job,data,ct));await db.SaveChangesAsync();
            check(transport.Edits==oldEdits+1&&transport.Replies==oldReplies,"historical delivered notices can upgrade their button without sending another address reply");
            var editRetry=await Fixture();await Process(editRetry.D);db.ChangeTracker.Clear();var editJob=await Notice(editRetry.C.Id);transport.EditResult=new(false,"fixture temporary edit error");var beforeEditReply=transport.Replies;var editFailed=false;
            try{await ShippingTrackingNotices.DeliverAsync(db,sender,opts,editJob,default,(data,ct)=>Mark(editJob,data,ct));}catch(InvalidOperationException){editFailed=true;}
            check(editFailed&&transport.Replies==beforeEditReply&&JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(editJob.Payload)!.AttemptedAt is null,"a failed idempotent button edit is retryable before any one-shot address reply");
            transport.EditResult=null;await ShippingTrackingNotices.DeliverAsync(db,sender,opts,editJob,default,(data,ct)=>Mark(editJob,data,ct));await db.SaveChangesAsync();
            check(transport.Replies==beforeEditReply+1&&JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(editJob.Payload)!.ButtonUpdated,"retrying the failed markup produces one notice and a successful green button");
            var parcel=await Fixture();var parcelId=Guid.NewGuid();var rootRow=await db.NotificationOutbox.SingleAsync(n=>n.CustomerId==parcel.C.Id&&n.Channel=="ShippingReferenceContext");
            rootRow.Payload=JsonSerializer.Serialize(new ShippingTrackingNotices.Source(parcel.C.Id,parcelId,[],chat,"root",parcel.Source,true));await db.SaveChangesAsync();
            await ShippingTrackingNotices.QueueAsync(db,opts,new(parcel.C.Id,chat,parcel.Source,parcelId),default);var parcelJob=await Notice(parcel.C.Id);
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=parcel.C.Id,Channel=ZibasheERP.Infrastructure.Services.ShippingPackApproval.ReceiptChannel,Recipient=parcelId.ToString("N"),CreatedAt=DateTime.UtcNow,Status=NotificationOutboxStatus.Processed,
                Payload=JsonSerializer.Serialize(new ZibasheERP.Infrastructure.Services.ShippingPackApprovalData{CustomerId=parcel.C.Id,RequestId=parcelId,ShippingChatId=chat,AddressMessageId=parcel.Source,SentAt=DateTime.UtcNow,PhotoFileIds=["fixture-1","fixture-2"],Deliveries=[new("-1009",0,SentAt:DateTime.UtcNow,MessageId:51),new("-1009",1,SentAt:DateTime.UtcNow,MessageId:52)]})});await db.SaveChangesAsync();
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,parcelJob,default,(data,ct)=>Mark(parcelJob,data,ct));await db.SaveChangesAsync();
            check(transport.Buttons.SelectMany(r=>r).Any(b=>b.Text==ShippingTrackingNotices.Text&&b.Style=="success")&&transport.Buttons.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:select:"+parcelId.ToString("N"))&&transport.Buttons.SelectMany(r=>r).Any(b=>b.Text=="📷 ارسال عکس سوم"),"parcel tracking preserves selection reports and the delivered photo count while replacing its tracking action with a green completion button");
            var directController=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",opts)})typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(directController,value);
            var directMethod=typeof(TelegramWebhookController).GetMethod("TryMarkDeliveredTrackingButtonAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var directReplies=transport.Replies;
            check(await (Task<bool>)directMethod.Invoke(directController,[parcel.C.Id,chat,parcel.Source,CancellationToken.None])!&&transport.Replies==directReplies&&transport.Buttons.SelectMany(r=>r).Any(b=>b.Text=="📷 ارسال عکس سوم"),"the immediate completion path uses the same green layout without replacing photo continuation or sending another notice");
            check(!await (Task<bool>)directMethod.Invoke(directController,[Guid.NewGuid(),chat,99999999L,CancellationToken.None])!,"a source without a successful tracking-delivery intent is never marked through the direct completion path");
            var missingButton=await Fixture();await Process(missingButton.D);db.ChangeTracker.Clear();var missingJob=await Notice(missingButton.C.Id);transport.EditResult=new(false,"message to edit not found");var beforeMissing=transport.Replies;
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,missingJob,default,(data,ct)=>Mark(missingJob,data,ct));await db.SaveChangesAsync();transport.EditResult=null;
            check(missingJob.Status==NotificationOutboxStatus.Failed&&transport.Replies==beforeMissing&&!JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(missingJob.Payload)!.ButtonUpdated,"a deleted address is terminal and cannot produce a standalone notice or false button success");
            var failed=await Fixture();transport.RejectPhoto=true;await Process(failed.D);transport.RejectPhoto=false;db.ChangeTracker.Clear();
            check(!await db.NotificationOutbox.AnyAsync(n=>n.CustomerId==failed.C.Id&&n.Channel==ShippingTrackingNotices.Channel),"a rejected tracking photo cannot create a successful-address notice");
            var invalid=await Fixture(false);
            check(!await ShippingTrackingNotices.QueueAsync(db,opts,new(invalid.C.Id,chat,invalid.Source,null),default),"an unknown source message cannot be marked as an address");
            check(!await ShippingTrackingNotices.QueueAsync(db,opts,new(good.C.Id,"-100123",good.Source,null),default)&&!await ShippingTrackingNotices.QueueAsync(db,opts,new(invalid.C.Id,chat,good.Source,null),default),"wrong-chat and another customer's address are refused");
            var older=await Fixture(old:true);await Process(older.D);db.ChangeTracker.Clear();
            check(await db.NotificationOutbox.AnyAsync(n=>n.CustomerId==older.C.Id&&n.Channel==ShippingTrackingNotices.Channel),"a uniquely bound older address is still marked without an arbitrary date limit");
            var ambiguous=await Fixture();db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=ambiguous.C.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=chat,Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddDays(-1),Payload=JsonSerializer.Serialize(new ShippingTrackingNotices.Source(ambiguous.C.Id,null,[],chat,"root",++next,true))});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            await Process(ambiguous.D);db.ChangeTracker.Clear();
            check(!await db.NotificationOutbox.AnyAsync(n=>n.CustomerId==ambiguous.C.Id&&n.Channel==ShippingTrackingNotices.Channel),"ambiguous legacy addresses are never marked by guessing which source message belongs to the code");
            var rate=await Fixture();await ShippingTrackingNotices.QueueAsync(db,opts,new(rate.C.Id,chat,rate.Source,null),default);db.ChangeTracker.Clear();job=await Notice(rate.C.Id);
            transport.ReplyResult=new(false,"429"){IsRetryable=true,RetryAfterSeconds=120};var start=DateTime.UtcNow;
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);await db.SaveChangesAsync();
            check(job.Status==NotificationOutboxStatus.Pending&&job.NextAttemptAt>=start.AddSeconds(120)&&JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(job.Payload)!.AttemptedAt==null,"a definite Telegram 429 preserves the full cooldown and permits a safe address-reply retry");
            transport.ReplyResult=null;await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);await db.SaveChangesAsync();check(job.Status==NotificationOutboxStatus.Processed,"address notice resumes after a definite cooldown");
            var interrupted=await Fixture();await ShippingTrackingNotices.QueueAsync(db,opts,new(interrupted.C.Id,chat,interrupted.Source,null),default);db.ChangeTracker.Clear();job=await Notice(interrupted.C.Id);
            job.Payload=JsonSerializer.Serialize(JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(job.Payload)! with{AttemptedAt=DateTime.UtcNow});await db.SaveChangesAsync();var before=transport.Replies;
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);await db.SaveChangesAsync();
            check(job.Status==NotificationOutboxStatus.Failed&&transport.Replies==before,"an interrupted unknown reply never produces an automatic duplicate under the address");
            var gone=await Fixture();await ShippingTrackingNotices.QueueAsync(db,opts,new(gone.C.Id,chat,gone.Source,null),default);db.ChangeTracker.Clear();job=await Notice(gone.C.Id);transport.ReplyResult=new(false,"message to reply not found");
            before=transport.Replies;await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);await db.SaveChangesAsync();transport.ReplyResult=null;
            check(job.Status==NotificationOutboxStatus.Failed&&transport.Replies==before+1,"a deleted address produces no standalone success message or blind retry");
            var drift=await Fixture();await ShippingTrackingNotices.QueueAsync(db,opts,new(drift.C.Id,chat,drift.Source,null),default);db.ChangeTracker.Clear();job=await Notice(drift.C.Id);job.Recipient="-100123";before=transport.Replies;
            await ShippingTrackingNotices.DeliverAsync(db,sender,opts,job,default);await db.SaveChangesAsync();check(job.Status==NotificationOutboxStatus.Failed&&transport.Replies==before,"a queued notice cannot move to a changed destination");
            var parallel=await Fixture();
            async Task<bool> QueueOther(){await using var other=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);return await ShippingTrackingNotices.QueueAsync(other,opts,new(parallel.C.Id,chat,parallel.Source,null),default);}
            await Task.WhenAll(QueueOther(),QueueOther());
            check(await db.NotificationOutbox.CountAsync(n=>n.CustomerId==parallel.C.Id&&n.Channel==ShippingTrackingNotices.Channel)==1,"concurrent completion events create one durable reply intent for an address");
            check(!await db.OrderItems.AnyAsync(i=>customers.Contains(i.Order!.CustomerId)),"notice processing creates no invoice items or fulfillment changes");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>customers.Contains(n.CustomerId)).ExecuteDeleteAsync();
            await db.TrackingDispatchDeliveries.Where(d=>batches.Contains(d.TrackingDispatch!.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>batches.Contains(d.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingImportBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();await db.Customers.Where(c=>customers.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
public class TrackingNoticeSender:DispatchProxy
{
    public int Photos,Replies,Edits;public IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Buttons=[];public bool RejectPhoto;public TelegramSendResult? ReplyResult,EditResult;public string? LastChat,LastText;public long LastReplyTo;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="SendPhotoBytesWithKeyboardAsync"){Photos++;return Task.FromResult(RejectPhoto?new TelegramSendResult(false,"rejected"):new(true,MessageId:900));}
        if(method.Name=="EditReplyMarkupAsync"){Edits++;Buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args![2]!;return Task.FromResult(EditResult??new TelegramSendResult(true));}
        if(method.Name=="SendReplyOnceAsync"){Replies++;LastChat=(string)args![0]!;LastText=(string)args[1]!;LastReplyTo=(long)args[2]!;return Task.FromResult(ReplyResult??new TelegramSendResult(true,MessageId:1000+Replies));}
        return Task.FromResult(new TelegramSendResult(true,MessageId:999));
    }
}
