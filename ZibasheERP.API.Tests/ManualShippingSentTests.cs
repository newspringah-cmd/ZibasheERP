using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class ManualShippingSentTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var sql=new SqlConnectionStringBuilder(connection);if(sql.InitialCatalog!="ZibasheERPCloudDev"||sql.DataSource!="127.0.0.1,14333")throw new Exception("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const string chat="-1004917788123";const long owner=741471928;var options=new TelegramOptions{OwnerUserId=owner.ToString(),ShippingChatId=chat};
        var ids=new List<Guid>();var batches=new List<Guid>();long next=84100;
        async Task<ManualShippingAddresses.Source> Fixture(bool valid=true)
        {
            var id=Guid.NewGuid();ids.Add(id);var customer=new Customer{Id=id,FullName="Synthetic manual address",Mobile="MANUAL-"+id.ToString("N")[..12],Notes=$"manual-address-registration:{owner}:{(valid?id:Guid.NewGuid()):N}",CreatedAt=DateTime.UtcNow.AddDays(-2)};
            var address=new Address{Id=Guid.NewGuid(),CustomerId=id,ReceiverName="نرگس امینی",FullAddress="تهران خیابان نمونه",CreatedAt=customer.CreatedAt};
            db.Customers.Add(customer);db.Addresses.Add(address);var root=new ManualShippingAddresses.Root(id,null,[],chat,"root",++next,true,address.Id);var row=new NotificationOutbox{Id=Guid.NewGuid(),CustomerId=id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=chat,Status=NotificationOutboxStatus.Processed,CreatedAt=customer.CreatedAt,Payload=JsonSerializer.Serialize(root)};db.NotificationOutbox.Add(row);await db.SaveChangesAsync();return new(row.Id,root,row.CreatedAt);
        }
        async Task<TrackingDispatch> Tracking(ManualShippingAddresses.Source source,bool ack,bool sentBefore=false)
        {
            var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),CreatedAt=source.CreatedAt.AddDays(-1)};batches.Add(batch.Id);db.TrackingImportBatches.Add(batch);
            var when=sentBefore?source.CreatedAt.AddHours(-1):DateTime.UtcNow;
            var dispatch=new TrackingDispatch{Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=source.Root.CustomerId,RecipientName="Synthetic postal recipient",TrackingCode=Guid.NewGuid().ToString("N"),Status=TrackingDispatchStatus.Sent,SentAt=when,CreatedAt=DateTime.UtcNow};db.TrackingDispatches.Add(dispatch);
            if(ack)db.TrackingDispatchDeliveries.Add(new(){Id=Guid.NewGuid(),TrackingDispatchId=dispatch.Id,TelegramChatId=owner.ToString(),SentAt=when,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();return dispatch;
        }
        var sender=DispatchProxy.Create<ITelegramMessageSender,ManualSentSender>();var transport=(ManualSentSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value)in new(string,object)[]{("_db",db),("_options",options),("_sender",sender)})typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,value);
        async Task<IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>> Buttons(ManualShippingAddresses.Source source,bool sent)=>
            await (Task<IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>>)typeof(TelegramWebhookController).GetMethod("ManualAddressButtonsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[source,sent,CancellationToken.None])!;
        async Task Call(ManualShippingAddresses.Source source,long message,string? target=null)
        {
            var callback=new TelegramCallbackQuery("test",new(owner,null),new TelegramMessage(null,new(long.Parse(target??chat),"supergroup"),new(owner,null),null,MessageId:message),"shipping:manualsent:"+source.ReferenceId.ToString("N"));
            await (Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleManualShippingSentAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[callback,CancellationToken.None])!;
        }
        try
        {
            var a=await Fixture();var b=await Fixture();var invalid=await Fixture(false);var sources=await ManualShippingAddresses.SourcesAsync(db,options,default);
            check(sources.Length==2&&sources.All(s=>s.ReferenceId!=invalid.ReferenceId),"manual shipping controls require validated dedicated registration metadata and exact bound address roots");
            var rows=await Buttons(a,false);
            check(rows.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:manualsent:"+a.ReferenceId.ToString("N"))&&rows.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:trackingcustomer:"+a.Root.CustomerId.ToString("N")),"a manual address includes independent mark-sent and tracking-photo controls plus existing reference buttons");
            await Call(a,a.Root.MessageId!.Value+100);await Call(a,a.Root.MessageId.Value,"-10012");
            check(!await ManualShippingAddresses.IsSentAsync(db,a,default)&&transport.Edits==0,"a manual sent button cannot close a different source message or another group");
            await Call(a,a.Root.MessageId.Value);await Call(a,a.Root.MessageId.Value);
            check(await ManualShippingAddresses.IsSentAsync(db,a,default)&&await db.NotificationOutbox.CountAsync(n=>n.Channel==ManualShippingAddresses.Channel&&n.CustomerId==a.Root.CustomerId)==1,"manual parcel completion is durable and idempotent across repeated clicks");
            check(transport.Buttons.Any(b=>b.Text=="✅ ارسال شد"&&b.Style=="success"),"manual completion turns the exact address sent button green");
            var ready=await TrackingPreparedAddresses.LoadForNameWarningsAsync(db,chat,default);
            check(ready.All(r=>r.MessageId!=a.Root.MessageId)&&ready.Any(r=>r.MessageId==b.Root.MessageId),"manually completed parcel leaves remaining-address and name-warning reports without closing a similar-name parcel");
            check(!await db.Invoices.AnyAsync(i=>ids.Contains(i.Order!.CustomerId))&&!await db.OrderItems.AnyAsync(i=>ids.Contains(i.Order!.CustomerId)),"marking legacy manual addresses sent never creates invoices or changes unrelated perfume items");
            var c=await Fixture();var cDispatch=await Tracking(c,true);sources=await ManualShippingAddresses.SourcesAsync(db,options,default);
            check(await ManualShippingAddresses.HasTrackingProofAsync(db,c,sources,options,default),"acknowledged manager tracking delivery closes its exact dedicated manual address even when the PDF batch predates the address");
            await (Task)typeof(TelegramWebhookController).GetMethod("QueueTrackingAddressNoticeAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[cDispatch,CancellationToken.None])!;
            check(await db.NotificationOutbox.AnyAsync(n=>n.Channel==ShippingTrackingNotices.Channel&&n.CustomerId==c.Root.CustomerId),"future PDF tracking delivery binds the manual source address despite an older batch timestamp");
            var unack=await Fixture();await Tracking(unack,false);sources=await ManualShippingAddresses.SourcesAsync(db,options,default);
            check(!await ManualShippingAddresses.HasTrackingProofAsync(db,unack,sources,options,default),"a claimed Sent dispatch without acknowledged manager delivery cannot auto-close a manual parcel");
            var old=await Fixture();await Tracking(old,true,true);sources=await ManualShippingAddresses.SourcesAsync(db,options,default);
            check(!await ManualShippingAddresses.HasTrackingProofAsync(db,old,sources,options,default),"an earlier tracking delivery cannot close a later manually registered address");
            await ManualShippingAddresses.CompleteAsync(db,c,null,"acknowledged-tracking-delivery",default);
            var deliveredRows=await Buttons(c,true);
            check(deliveredRows.SelectMany(r=>r).Any(b=>b.Text==ShippingTrackingNotices.Text&&b.Style=="success"),"acknowledged manual tracking preserves a separate green tracking-sent indication");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>ids.Contains(n.CustomerId)).ExecuteDeleteAsync();await db.TrackingDispatchDeliveries.Where(d=>batches.Contains(d.TrackingDispatch!.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>batches.Contains(d.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingImportBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();await db.Addresses.Where(a=>ids.Contains(a.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
public class ManualSentSender:DispatchProxy
{
    public int Edits;public TelegramInlineButton[] Buttons=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="AnswerCallbackAsync")return Task.FromResult(new TelegramSendResult(true));
        if(method.Name=="EditReplyMarkupAsync"){Edits++;Buttons=((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args![2]!).SelectMany(r=>r).ToArray();return Task.FromResult(new TelegramSendResult(true));}
        throw new InvalidOperationException("Manual completion cannot send tracking, customer messages or financial documents");
    }
}
