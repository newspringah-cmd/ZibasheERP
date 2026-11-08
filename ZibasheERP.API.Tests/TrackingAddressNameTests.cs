using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class TrackingAddressNameTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var registration=new Customer{Id=Guid.NewGuid()};registration.Notes="manual-address-registration:1907823600:"+registration.Id.ToString("N");
        check(ZibasheERP.Infrastructure.Services.ManualShippingRegistration.TrackingDestination(registration,"741471928")=="741471928","manual address tracking always targets the configured owner, including registrations by another accountant");
        check(ZibasheERP.Infrastructure.Services.ManualShippingRegistration.TrackingDestination(new Customer{FullName="ثبت دستی"},"741471928") is null,"a display name alone cannot redirect an ordinary customer's tracking to the manager");
        var refused=false;try{ZibasheERP.Infrastructure.Services.ManualShippingRegistration.TrackingDestination(registration,"");}catch(InvalidOperationException){refused=true;}
        check(refused,"a missing manager destination cannot silently fall back to the registrar");
        registration.Notes="manual-address-registration:1907823600:"+Guid.NewGuid().ToString("N");refused=false;
        try{ZibasheERP.Infrastructure.Services.ManualShippingRegistration.TrackingDestination(registration,"741471928");}catch(InvalidOperationException){refused=true;}
        check(refused,"a manual-registration marker belonging to another customer is rejected");
        var id=Guid.NewGuid();
        foreach(var raw in new[]{"نام : نرگس بهرامی","نام و نام خانوادگی: نرگس بهرامی","نام گیرنده:\nنرگس بهرامی\nموبایل: 09121111111",
            "تهران خیابان نمونه پلاک ۲ بنام نرگس بهرامی","تهران، کوچه نمونه. برسد به نرگس بهرامی.","نرگس بهرامی/آدرس: خیابان نمونه/کدپستی: 1111111111",
            "تحویل گیرنده: نرگس بهرامی، تهران","نام‌و‌نام‌خانوادگی: نرگس بهرامی","نام: خانم نرگس بهرامی"})
            check(TrackingMatchingTests.Select("نرگس بهرامی","",[TrackingMatchingTests.Row(id,"@fixture",raw)])?.CustomerId==id,"tracking extracts a full receiver from a bounded labelled/address segment: "+raw.Replace('\n',' '));
        foreach(var raw in new[]{"تهران خیابان نرگس بهرامی کوچه نمونه پلاک ۲","نام : @fixture","کدپستی 1111111111\nشماره تماس 09121111111","بلوار بهرامی\nنام: پریسا محمدی"})
            check(TrackingMatchingTests.Select("نرگس بهرامی","",[TrackingMatchingTests.Row(id,"@fixture",raw)]) is null,"tracking refuses street names, usernames and contact fields as receiver evidence");
        check(TrackingMatchingTests.Select("بهرامی","",[TrackingMatchingTests.Row(id,"@fixture","نام: نرگس بهرامی")]) is null,"surname-only rows are never upgraded automatically by the broader address parser");
        check(TrackingMatchingTests.Select("نرگس بهرامی","",[TrackingMatchingTests.Row(id,"@a","نام: نرگس بهرامی"),TrackingMatchingTests.Row(Guid.NewGuid(),"@b","نام: نرگس بهرامی")]) is null,
            "identical full names on unrelated global addresses remain ambiguous");
        var wordMethod=typeof(TrackingImportService).GetMethod("SimilarRecipientWords",BindingFlags.Static|BindingFlags.NonPublic)!;
        check((int)wordMethod.Invoke(null,["نرگس بهرامی","نرگس بهرامی"])! ==2,"advisory suggestions recognize both shared name words");
        check((int)wordMethod.Invoke(null,["نرگس بهرامی","پریسا بهرامی"])! ==1,"advisory suggestions recognize one shared surname without automatic approval");
        check((int)wordMethod.Invoke(null,["نرگس بهرامی","مریم کریمی"])! ==0,"unrelated recipient words are not labelled as a word match");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new Exception("Development database only");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long op=741471928,ship=-10077112231,chat=op;
        var marker="tracking_address_"+Guid.NewGuid().ToString("N")[..10];
        var a=new Customer{Id=Guid.NewGuid(),FullName=marker,Username=marker,Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var b=new Customer{Id=Guid.NewGuid(),FullName="ثبت دستی قدیمی",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var missing=new Customer{Id=Guid.NewGuid(),FullName="ثبت دستی",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        Address Address(Customer c,string name)=>new(){Id=Guid.NewGuid(),CustomerId=c.Id,ReceiverName="@fixture",FullAddress="تهران خیابان نمونه پلاک ۲\nنام : "+name,Mobile="09121111111",CreatedAt=DateTime.UtcNow};
        var address=Address(a,"نرگس بهرامی");var old=Address(b,"نرگس بهرامی");var absent=Address(missing,"منصور پارسا");
        var group=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=a.Id,ChatId="-10077112232",IsActive=true,Title=marker,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        var root=Guid.NewGuid();var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),RequestedByTelegramUserId=op,SourceChatId=chat,CreatedAt=DateTime.UtcNow};
        TrackingDispatch Dispatch(string name)=>new(){Id=Guid.NewGuid(),ImportBatchId=batch.Id,Carrier=TrackingCarrier.IranPost,RecipientName=name,TrackingCode=Random.Shared.NextInt64(100000000000000,999999999999999).ToString(),Status=TrackingDispatchStatus.NeedsReview,CardImage=[1,2],CreatedAt=DateTime.UtcNow};
        var dispatch=Dispatch("بهرامی");var changed=Dispatch("بهرامی");var attempted=Dispatch("بهرامی");attempted.SendingStartedAt=DateTime.UtcNow;
        var opts=new TelegramOptions{OwnerUserId=op.ToString(),ShippingChatId=ship.ToString()};
        using var service=new TrackingImportService(db,Options.Create(new AddressLabelOptions{OpenAiApiKey="synthetic"}),null!,NullLogger<TrackingImportService>.Instance,Options.Create(opts));
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingReferenceSender>();var sent=(ShippingReferenceSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",opts),("_trackingImportService",service),("_logger",NullLogger<TelegramWebhookController>.Instance),("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task Call(string data,long actor=op,long source=chat)=>await (Task)typeof(TelegramWebhookController).GetMethod("TryHandleTrackingImportCallbackCoreAsync",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(controller,[new TelegramCallbackQuery("fixture",new(actor,null),new TelegramMessage(null,new(source,"private"),new(actor,null),null,MessageId:500),data),CancellationToken.None])!;
        string Button(string prefix)=>sent.Calls.SelectMany(c=>c.Buttons??[]).SelectMany(r=>r).Last(b=>b.CallbackData?.StartsWith(prefix)==true).CallbackData!;
        string RootPayload(Guid owner,Guid addressId)=>JsonSerializer.Serialize(new{CustomerId=owner,AddressId=addressId,RequestId=(Guid?)null,ItemIds=Array.Empty<Guid>(),Kind="root",Installed=true,ChatId=ship.ToString(),MessageId=1000L});
        try
        {
            db.AddRange(a,b,missing,address,old,absent,group,batch,dispatch,changed,attempted);
            db.NotificationOutbox.Add(new(){Id=root,CustomerId=a.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=ship.ToString(),Payload=RootPayload(a.Id,address.Id),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            var exact=await service.MatchAsync("نرگس بهرامی","",default);
            check(exact.Status==TrackingDispatchStatus.Ready&&exact.CustomerId==a.Id&&exact.ShippingRequestId is null,"exact posted preparation address resolves a connected legacy customer despite obsolete duplicate address profiles");
            check((await service.MatchAsync("بهرامی","",default)).Status==TrackingDispatchStatus.NeedsReview,"prepared-address preference does not automatically accept a surname");
            check((await service.MatchAsync("منصور پارسا","",default)) is {Status:TrackingDispatchStatus.NeedsReview,CustomerId:not null},"a named manual address without a customer identity or linked group is labelled for destination review rather than marked ready");
            await db.Customers.Where(c=>c.Id==missing.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Notes,"manual-address-registration:1907823600:"+missing.Id.ToString("N")));
            check((await service.MatchAsync("منصور پارسا","",default)) is {Status:TrackingDispatchStatus.Ready,CustomerId:not null},"an exact full-name manual registration has an explicit owner destination without requiring a customer group");
            var manualTargets=await (Task<string[]>)typeof(TelegramWebhookController).GetMethod("ResolveTrackingDestinationChatIdsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(controller,[new TrackingDispatch{CustomerId=missing.Id},CancellationToken.None])!;
            check(manualTargets.SequenceEqual([op.ToString()]),"matched manual PDF tracking targets only the same configured owner as manual photo tracking");
            using(var unconfigured=new TrackingImportService(db,Options.Create(new AddressLabelOptions{OpenAiApiKey="synthetic"}),null!,NullLogger<TrackingImportService>.Instance,Options.Create(new TelegramOptions{ShippingChatId=ship.ToString()})))
                check((await unconfigured.MatchAsync("منصور پارسا","",default)).Status==TrackingDispatchStatus.NeedsReview,"a manual PDF match stays under review when the owner destination is not configured");
            await db.Customers.Where(c=>c.Id==missing.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Notes,(string?)null));
            var loaded=await TrackingPreparedAddresses.LoadAsync(db,ship.ToString(),default);
            check(loaded.Length==1&&loaded[0].ReferenceId==root,"candidate list contains only installed owner-matching prepared addresses with active accounting-group links");
            check((await TrackingPreparedAddresses.LoadAsync(db,ship.ToString(),default,batch.CreatedAt)).Length==0,
                "automatic rematching cannot use a preparation address posted after the original tracking batch");
            check((await service.MatchAsync("نرگس بهرامی","",default,batch.CreatedAt)).Status==TrackingDispatchStatus.NeedsReview,
                "a later preparation message cannot silently disambiguate an earlier postal file");
            await db.NotificationOutbox.Where(n=>n.Id==root).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,RootPayload(b.Id,address.Id)));
            check((await TrackingPreparedAddresses.LoadAsync(db,ship.ToString(),default)).Length==0,"forged preparation customer metadata cannot borrow another customer's address");
            await db.NotificationOutbox.Where(n=>n.Id==root).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,RootPayload(a.Id,address.Id)));
            await Call("trackingimport:select-list:"+batch.Id.ToString("N")+":0");
            check(sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync").Buttons!.SelectMany(r=>r).Count(b=>b.CallbackData?.StartsWith("trackingimport:select-open:")==true)==2,
                "batch summary opens an actionable review list without requiring the PDF or old tracking cards to be resent");
            check(sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync").Text.Contains("شباهت نام با @"+a.Username)&&sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync").Text.Contains("نرگس بهرامی"),
                "uncertain rows display the similar receiver name and clickable Telegram username for administrator checking");
            var hinted=(string)typeof(TelegramWebhookController).GetMethod("TrackingCaptionWithSuggestions",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[dispatch,new string('x',1000),loaded])!;
            check(hinted.Length<=1024&&hinted.Contains(dispatch.TrackingCode)&&hinted.Contains("@"+a.Username),"similarity hints fit postal-photo caption limits while retaining exact tracking digits");
            sent.Admin=false;await Call("trackingimport:select-open:"+dispatch.Id.ToString("N"),999);sent.Admin=true;
            check(!await db.NotificationOutbox.AnyAsync(n=>n.Channel=="TrackingRecipientSelection"&&n.Payload.Contains(dispatch.Id.ToString())),"unauthorized users cannot start manual tracking recipient selection");
            await Call("trackingimport:send:"+dispatch.Id.ToString("N"));
            check((await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id)).Status==TrackingDispatchStatus.NeedsReview&&
                !await db.NotificationOutbox.AnyAsync(n=>n.Id==dispatch.Id&&n.Channel=="TrackingDispatch"),
                "Send on an older unresolved tracking card opens recipient checking rather than delivering to an unapproved suggestion");
            await Call("trackingimport:select-open:"+dispatch.Id.ToString("N"));await Call(Button("trackingimport:select-pick:"));var stale=Button("trackingimport:select-ok:");
            await Call(Button("trackingimport:select-pick:"));var confirm=Button("trackingimport:select-ok:");await Call(stale);
            check((await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id)).Status==TrackingDispatchStatus.NeedsReview,"an obsolete confirmation token cannot silently approve the newly selected recipient");
            await Call(confirm,source:chat-1);await Call(confirm,actor:999);
            check((await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id)).CustomerId is null,"tracking selection is bound to its original operator and conversation");
            await Call(confirm);await Call(confirm);
            var selected=await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id);
            check(selected.Status==TrackingDispatchStatus.Ready&&selected.CustomerId==a.Id&&selected.ShippingRequestId is null&&selected.RecipientName=="بهرامی","explicit reviewed selection can prepare a surname-only legacy row while preserving its original OCR name and tracking card");
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="TrackingRecipientChoiceAudit"&&n.Payload.Contains(dispatch.Id.ToString()))==1,"repeated confirmation retains one durable manual-selection audit receipt");
            check(!await db.NotificationOutbox.AnyAsync(n=>n.Channel=="TrackingDispatch"&&n.Id==dispatch.Id),"manual recipient approval never starts code delivery without a separate explicit Send");
            await Call("trackingimport:rebuild:"+dispatch.Id.ToString("N"));
            check((await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==dispatch.Id)).Status==TrackingDispatchStatus.Ready,"rebuild cannot erase an explicitly reviewed manual recipient selection");
            check(sent.Calls.SelectMany(c=>c.Buttons??[]).SelectMany(r=>r).Where(b=>b.CallbackData is not null).All(b=>System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!)<=64),"all recipient-selection and versioned confirmation callbacks fit Telegram's byte limit");
            await Call("trackingimport:select-open:"+changed.Id.ToString("N"));await Call(Button("trackingimport:select-pick:"));confirm=Button("trackingimport:select-ok:");
            await db.Addresses.Where(a=>a.Id==address.Id).ExecuteUpdateAsync(s=>s.SetProperty(a=>a.FullAddress,"نام: شخص دیگر"));await Call(confirm);
            check((await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==changed.Id)).Status==TrackingDispatchStatus.NeedsReview,"changing an address after preview invalidates the manual tracking confirmation");
            var original=address.FullAddress;await db.Addresses.Where(a=>a.Id==address.Id).ExecuteUpdateAsync(s=>s.SetProperty(a=>a.FullAddress,original));
            var frames=await db.NotificationOutbox.CountAsync(n=>n.Channel=="TrackingRecipientSelection");await Call("trackingimport:select-open:"+attempted.Id.ToString("N"));
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="TrackingRecipientSelection")==frames,"a dispatch with any prior send-start intent cannot be retargeted");
            var sentRow=Dispatch("نرگس بهرامی");sentRow.CustomerId=a.Id;sentRow.Status=TrackingDispatchStatus.Sent;sentRow.SentAt=DateTime.UtcNow;db.TrackingDispatches.Add(sentRow);await db.SaveChangesAsync();
            check((await TrackingPreparedAddresses.LoadAsync(db,ship.ToString(),default)).Length==0,"a legacy prepared address already followed by delivered tracking is excluded from fresh candidate suggestions");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>n.Id==root||n.CustomerId==a.Id||n.Channel=="TrackingRecipientSelection"&&n.Payload.Contains(batch.Id.ToString())).ExecuteDeleteAsync();
            var dispatchIds=await db.TrackingDispatches.Where(d=>d.ImportBatchId==batch.Id).Select(d=>d.Id.ToString()).ToArrayAsync();
            foreach(var key in dispatchIds)await db.NotificationOutbox.Where(n=>n.Channel=="TrackingRecipientSelection"&&n.Payload.Contains(key)).ExecuteDeleteAsync();
            await db.TrackingImportBatches.Where(b=>b.Id==batch.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>g.Id==group.Id).ExecuteDeleteAsync();
            var ids=new[]{a.Id,b.Id,missing.Id};await db.Addresses.Where(a=>ids.Contains(a.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
