using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class SharedShippingTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long admin=741471928;var stamp=Guid.NewGuid().ToString("N")[..12];var now=DateTime.UtcNow;
        var customers=Enumerable.Range(0,4).Select(n=>new Customer{Id=Guid.NewGuid(),FullName="Shared fixture "+n,Username="shared_"+stamp+n,
            TelegramId=(850000000L+Random.Shared.Next(10000000)+n).ToString(),Mobile="09"+stamp+n,CurrentDebt=100000+n,CreatedAt=now}).ToArray();
        var groups=customers.Select((c,n)=>new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=c.Id,ChatId=(-1000000000000L-Random.Shared.Next(100000000)-n).ToString(),
            Title="Synthetic shared parcel",IsActive=true,LinkedAt=now,CreatedAt=now}).ToArray();
        var address=new Address{Id=Guid.NewGuid(),CustomerId=customers[0].Id,ReceiverName="Synthetic primary",Mobile="09120000000",City="تهران",FullAddress="Synthetic address only",PostalCode="0123456789",CreatedAt=now};
        db.Customers.AddRange(customers);db.CustomerTelegramGroups.AddRange(groups);db.Addresses.Add(address);await db.SaveChangesAsync();
        var orders=new List<Guid>();var roots=new List<Guid>();var requests=new List<Guid>();var batches=new List<Guid>();var operations=new List<Guid>();
        var sender=DispatchProxy.Create<ITelegramMessageSender,SharedShippingSender>();var transport=(SharedShippingSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)!=null)field.SetValue(controller,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=admin.ToString(),ShippingChatId="-100555001"}),
            ("_logger",NullLogger<TelegramWebhookController>.Instance),("_orderFlowDrafts",new TelegramOrderFlowDraftStore()),("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task<object?> Call(string method,params object?[] args)
        {var task=(Task)typeof(TelegramWebhookController).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        async Task Input(string text,long user=admin)=>await Call("TryHandleSharedShippingMessageAsync",new TelegramMessage(text,new(user,"private"),new(user,null),null,MessageId:10),CancellationToken.None);
        async Task Select(string action,long user=admin,string type="private")=>await Call("TryHandleSharedShippingCallbackAsync",new TelegramCallbackQuery("shared",new(user,null),new TelegramMessage(null,new(user,type),new(user,null),null,MessageId:11),"shipping:shared:"+action),CancellationToken.None);
        async Task<OrderItem> Item(int customer,bool ready=true,Guid? parcel=null,bool inventory=false)
        {
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customers[customer].Id,OrderNumber="SHARED-"+Guid.NewGuid().ToString("N"),Status=OrderStatus.Invoiced,
                IsInventory=inventory,CreatedAt=now,FinalAmount=300000,InvoiceIssuedAt=DateTime.UtcNow,DeliveryAddressId=parcel.HasValue?address.Id:null};orders.Add(order.Id);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,RequestedVolumeMl=3,ManualDescription="عطر مشتری "+customer,PerfumeAmount=300000,LineTotal=300000,
                FulfillmentStatus=ready?OrderItemFulfillmentStatus.DecantedReadyToShip:OrderItemFulfillmentStatus.DecantQueue,ShippingRequestId=parcel,
                ShippingRequestedAt=parcel.HasValue?now.AddMinutes(-10):null,ShippingTelegramMessageId=parcel.HasValue?800:null,CreatedAt=DateTime.UtcNow};
            db.Orders.Add(order);db.OrderItems.Add(item);db.Invoices.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber="SHAREDINV-"+Guid.NewGuid().ToString("N"),
                Status=InvoiceStatus.Paid,TotalAmount=300000,IssuedAt=DateTime.UtcNow,CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();return item;
        }
        async Task<Guid> Root(Guid request,long message=800)
        {
            var id=Guid.NewGuid();roots.Add(id);requests.Add(request);
            db.NotificationOutbox.Add(new(){Id=id,CustomerId=customers[0].Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient="-100555001",Status=NotificationOutboxStatus.Processed,
                CreatedAt=DateTime.UtcNow,Payload=JsonSerializer.Serialize(new{CustomerId=customers[0].Id,RequestId=request,ItemIds=Array.Empty<Guid>(),ChatId="-100555001",Kind="root",MessageId=message,Installed=true,AddressId=address.Id})});
            await db.SaveChangesAsync();return id;
        }
        async Task<TrackingDispatch> Dispatch(Guid request)
        {
            var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),Carrier=TrackingCarrier.IranPost,CreatedAt=now,ConfirmedAt=DateTime.UtcNow};batches.Add(batch.Id);
            var dispatch=new TrackingDispatch{Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=customers[0].Id,ShippingRequestId=request,TrackingCode=Guid.NewGuid().ToString("N"),
                RecipientName="Synthetic primary",Status=TrackingDispatchStatus.Ready,CardImage=[1,2,3],CreatedAt=now};db.Add(batch);db.Add(dispatch);await db.SaveChangesAsync();db.ChangeTracker.Clear();return dispatch;
        }
        async Task Process(TrackingDispatch d)=>await Call("ProcessTrackingBatchAsync",-100555001L,d.ImportBatchId,CancellationToken.None,d.Id,true);
        async Task<OrderItem> Reload(Guid id)=>await db.OrderItems.AsNoTracking().SingleAsync(i=>i.Id==id);
        var service=new SharedShippingService(db);
        try
        {
            var request=Guid.NewGuid();var primary=await Item(0,parcel:request);var second=await Item(1);var third=await Item(2);var unready=await Item(1,false);var unrelated=await Item(3);
            var gift=await Item(3);
            await db.Orders.Where(o=>o.Id==gift.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Source,OrderSource.ManualInvoice).SetProperty(o=>o.Notes,ShippingRecipientPolicy.ManualGiftMarker+customers[2].Username));
            db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),OrderId=gift.OrderId,CustomerId=customers[2].Id,Channel="Telegram",EventType="GiftInvoiceIssued",Recipient=groups[2].ChatId,Payload="{}",Status=NotificationOutboxStatus.Processed,CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var root=await Root(request);
            await Select("start",admin+1);check(transport.Texts.Last().Contains("فقط برای مدیر"),"shared post configuration rejects an unauthorized customer");
            await Select("start");await Input("@"+customers[0].Username);await Input("@"+customers[1].Username+"\n@"+customers[2].Username);
            check(transport.Texts.Any(t=>t.Contains(customers[1].Username!)&&t.Contains(customers[2].Username!))&&!(await Reload(second.Id)).ShippingRequestId.HasValue,
                "shared post previews ready items separately for every username without mutating ownership before confirmation");
            var oldButton=transport.Buttons.Last(b=>b.StartsWith("shipping:shared:confirm:"));await Select("cancel");await Select(oldButton["shipping:shared:".Length..]);
            check((await Reload(second.Id)).ShippingRequestId is null,"cancelled shared-post preview cannot later assign items through an old button");
            var preview=await service.PreviewAsync(request,customers[0].Id,address.Id,[customers[1].Id,customers[2].Id],admin);
            await db.OrderItems.Where(i=>i.Id==second.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.RequestedVolumeMl,4m));
            var stale=false;try{await service.ConfirmAsync(preview,admin,800,default,root);}catch(InvalidOperationException){stale=true;}
            check(stale&&(await Reload(second.Id)).ShippingRequestId is null,"changed quantities invalidate shared parcel preview without partially assigning companions");
            preview=await service.PreviewAsync(request,customers[0].Id,address.Id,[customers[1].Id,customers[2].Id],admin);
            await service.ConfirmAsync(preview,admin,800,default,root);db.ChangeTracker.Clear();
            check((await Reload(second.Id)).ShippingRequestId==request&&(await Reload(third.Id)).ShippingRequestId==request&&
                (await Reload(unready.Id)).ShippingRequestId is null&&(await Reload(unrelated.Id)).ShippingRequestId is null,
                "explicit shared parcel binds all companions' ready items and excludes unready and nonmember items");
            check(await db.Orders.Where(o=>orders.Contains(o.Id)).AllAsync(o=>o.CustomerId==customers[0].Id||o.CustomerId==customers[1].Id||o.CustomerId==customers[2].Id||o.CustomerId==customers[3].Id)&&
                (await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==second.OrderId)).CustomerId==customers[1].Id&&
                (await db.Customers.AsNoTracking().SingleAsync(c=>c.Id==customers[1].Id)).CurrentDebt==100001,
                "shared shipping preserves financial ownership, original paid invoices and separate customer debts");
            var refresh=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Channel=="SharedShippingRefresh"&&n.CustomerId==customers[0].Id);
            await Call("RefreshSharedShippingAddressAsync",refresh,CancellationToken.None);
            check(transport.Edits.Any(t=>t.Message==800&&t.Text.Contains(customers[1].Username!)&&t.Text.Contains(customers[2].Username!))&&transport.AddressesSent==0,
                "shared post edits the original address with per-customer item blocks instead of posting additional addresses");
            var load=typeof(TelegramWebhookController).GetNestedType("ShippingReferenceContext",BindingFlags.NonPublic)!;
            var context=JsonSerializer.Deserialize((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==root)).Payload,load)!;
            var loaded=(OrderItem[])(await Call("LoadShippingReferenceItemsAsync",context,CancellationToken.None))!;
            check(loaded.Select(i=>i.Id).ToHashSet().SetEquals([primary.Id,second.Id,third.Id,gift.Id]),"address photo and completed-list controls resolve the complete shared parcel");
            await db.OrderItems.Where(i=>i.Id==unrelated.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippingRequestId,(Guid?)request));
            var rejected=false;try{await ShippingPackageService.ValidatePackageAsync(db,request);}catch(ShippingRecipientValidationException){rejected=true;}
            check(rejected,"an unapproved customer's item cannot bypass strict shared-parcel recipient validation");
            await db.OrderItems.Where(i=>i.Id==unrelated.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippingRequestId,(Guid?)null));
            var late=await Item(1,inventory:true);await ShippingPackageService.AttachInventoryAsync(db,customers[1].Id);db.ChangeTracker.Clear();
            check((await Reload(late.Id)).ShippingRequestId==request,"newly invoiced ready inventory joins the member's sole open shared parcel");
            check((await Reload(gift.Id)).ShippingRequestId==request&&(await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==gift.OrderId)).CustomerId==customers[3].Id,
                "a gift joins its actual recipient's shared parcel while the giver's other items stay outside");
            var approvalId=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=approvalId,CustomerId=customers[0].Id,Channel=ShippingPackApproval.ReceiptChannel,Recipient=groups[0].ChatId,
                Payload=JsonSerializer.Serialize(new ShippingPackApprovalData{CustomerId=customers[0].Id,RequestId=request,ItemIds=[primary.Id],State=ShippingPackApprovalState.Confirmed}),Status=NotificationOutboxStatus.Processed,CreatedAt=now});await db.SaveChangesAsync();
            var outdatedApproval=false;try{await ShippingPackageService.ValidateDispatchAsync(db,request,customers[0].Id);}catch(ShippingRecipientValidationException){outdatedApproval=true;}
            check(!outdatedApproval,"shared tracking does not depend on an older photo approval covering only the primary customer");
            await db.NotificationOutbox.Where(n=>n.Id==approvalId).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.IsDeleted,true));db.ChangeTracker.Clear();
            var dispatch=await Dispatch(request);
            await db.CustomerTelegramGroups.Where(g=>g.Id==groups[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,false));
            var missingGroup=false;try{await Call("SharedTrackingDestinationsAsync",dispatch,CancellationToken.None);}catch(ShippingRecipientValidationException){missingGroup=true;}
            check(missingGroup&&transport.Accepted.Count==0,"missing companion group is caught before any shared tracking card is sent");
            await db.CustomerTelegramGroups.Where(g=>g.Id==groups[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.IsActive,true));
            transport.RejectOnceChat=groups[1].ChatId;
            var throttled=false;try{await Process(dispatch);}catch(TrackingDispatchRetryException){throttled=true;}db.ChangeTracker.Clear();
            check(throttled&&!await db.OrderItems.AnyAsync(i=>i.ShippingRequestId==request&&i.ShippedAt!=null),"partial shared tracking delivery preserves every item until all destinations accept the same card");
            var frozen=false;try{await service.PreviewAsync(request,customers[0].Id,address.Id,[customers[1].Id,customers[2].Id],admin);}catch(InvalidOperationException){frozen=true;}
            check(frozen,"shared membership cannot change after a partial tracking delivery has started");
            await Process(dispatch);db.ChangeTracker.Clear();
            check(groups.Take(3).All(g=>transport.Accepted.Count(x=>x==g.ChatId)==1)&&!transport.Accepted.Contains(groups[3].ChatId),
                "tracking reaches all three approved customer groups exactly once across a definite 429 retry");
            check(await db.OrderItems.Where(i=>i.ShippingRequestId==request).AllAsync(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped&&i.ShippedAt!=null)&&
                (await Reload(unready.Id)).ShippedAt is null&&(await Reload(unrelated.Id)).ShippedAt is null,
                "main recipient tracking completion ships only every ready item in the exact shared parcel");
            check((await SharedShippingService.ReadAsync(db,request))!.ClosedAt!=null,"completed shared parcel is durably closed against later additions");
            var after=await Item(1,inventory:true);await ShippingPackageService.AttachInventoryAsync(db,customers[1].Id);await ShippingPackageService.CompleteAsync(db,request,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(after.Id)).ShippingRequestId is null&&(await Reload(after.Id)).ShippedAt is null,"tracking replay cannot absorb a member's inventory invoiced after the shared parcel closed");

            // A second shared parcel verifies the photo-under-address entry point
            // uses the same durable fanout and per-destination delivery receipts.
            var manualRequest=Guid.NewGuid();var manualPrimary=await Item(0,parcel:manualRequest);var manualRoot=await Root(manualRequest,801);
            var manualPreview=await service.PreviewAsync(manualRequest,customers[0].Id,address.Id,[customers[1].Id],admin);
            await service.ConfirmAsync(manualPreview,admin,801,default,manualRoot);db.ChangeTracker.Clear();
            var draft=new TelegramShippingTrackingPhotoDraft{ChatId=-100555001,UserId=admin,CustomerId=customers[0].Id,ShippingRequestId=manualRequest,SourceMessageId=801};
            var message=new TelegramMessage(null,new(-100555001,"supergroup"),new(admin,null),null,MessageId:802);
            await Call("QueueSharedTrackingPhotoAsync",message,draft,"synthetic-tracking-photo",CancellationToken.None);
            var manualDispatch=await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.ShippingRequestId==manualRequest);batches.Add(manualDispatch.ImportBatchId);operations.Add(manualDispatch.Id);
            check(await db.NotificationOutbox.AnyAsync(n=>n.Id==manualDispatch.Id&&n.Channel=="TrackingDispatch"),"manual shared tracking photo queues durable delivery instead of transient direct sends");
            transport.Accepted.Clear();await Process(manualDispatch);db.ChangeTracker.Clear();
            check(transport.Accepted.Order().SequenceEqual(groups.Take(2).Select(g=>g.ChatId).Order())&&(await Reload(manualPrimary.Id)).ShippedAt!=null,
                "photo tracking under the main address fans out to every member and completes their shared ready items");
            var addressOnlyRoot=await Root(Guid.NewGuid(),803);
            var raw=await db.NotificationOutbox.SingleAsync(n=>n.Id==addressOnlyRoot);
            var rawContext=System.Text.Json.Nodes.JsonNode.Parse(raw.Payload)!.AsObject();rawContext["RequestId"]=null;raw.Payload=rawContext.ToJsonString();await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var soleMemberItem=await Item(2);
            await Select("start");await Input("@"+customers[0].Username);await Input("@"+customers[2].Username);
            var addressOnlyConfirm=transport.Buttons.Last(b=>b.StartsWith("shipping:shared:confirm:"));await Select(addressOnlyConfirm["shipping:shared:".Length..]);db.ChangeTracker.Clear();
            var newRequest=(await Reload(soleMemberItem.Id)).ShippingRequestId;
            check(newRequest.HasValue&&(await SharedShippingService.ReadAsync(db,newRequest.Value))!.PrimaryCustomerId==customers[0].Id,
                "an already-posted address can anchor a shared parcel even when the primary customer has no ready items");
            var latePrimary=await Item(0,inventory:true);await ShippingPackageService.AttachInventoryAsync(db,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(latePrimary.Id)).ShippingRequestId==newRequest,"new ready inventory joins an address-only primary's open shared parcel without requiring an earlier owned item");
            await ShippingPackageService.CompleteAsync(db,newRequest,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(soleMemberItem.Id)).ShippedAt!=null&&(await Reload(latePrimary.Id)).ShippedAt!=null,"address-only primary completion still ships every approved member's exact ready item");

            var legacyRequest=Guid.NewGuid();requests.Add(legacyRequest);
            var legacyPrimary=await Item(0,parcel:legacyRequest);var newCompanion=await Item(1);
            var latestAddress=await Root(Guid.NewGuid(),804);
            var latestRow=await db.NotificationOutbox.SingleAsync(n=>n.Id==latestAddress);
            var latestContext=System.Text.Json.Nodes.JsonNode.Parse(latestRow.Payload)!.AsObject();latestContext["RequestId"]=null;
            latestRow.Payload=latestContext.ToJsonString();await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var resolved=await service.ResolvePrimaryRequestAsync(customers[0].Id,null);
            check(resolved==legacyRequest&&!db.ChangeTracker.HasChanges(),
                "a newly posted address reuses the primary customer's unique existing ready parcel without changing items");
            var buttonCount=transport.Buttons.Count(b=>b.StartsWith("shipping:shared:confirm:"));
            await Select("start");await Input("@"+customers[0].Username);await Input("@"+customers[1].Username);
            check(transport.Buttons.Count(b=>b.StartsWith("shipping:shared:confirm:"))==buttonCount+1&&
                (await Reload(legacyPrimary.Id)).ShippingRequestId==legacyRequest&&(await Reload(newCompanion.Id)).ShippingRequestId==null,
                "address-first shared shipping previews companions against the old primary parcel without premature reassignment");
            var reuseConfirm=transport.Buttons.Last(b=>b.StartsWith("shipping:shared:confirm:"));
            await Select(reuseConfirm["shipping:shared:".Length..]);db.ChangeTracker.Clear();
            var installed=JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==latestAddress)).Payload);
            check((await Reload(legacyPrimary.Id)).ShippingRequestId==legacyRequest&&(await Reload(newCompanion.Id)).ShippingRequestId==legacyRequest&&
                (await Reload(legacyPrimary.Id)).ShippingTelegramMessageId==804&&installed.RootElement.GetProperty("RequestId").GetGuid()==legacyRequest,
                "confirmation binds the newly selected address and companion to the same primary parcel while retaining its request identity");
            check((await db.Orders.AsNoTracking().SingleAsync(o=>o.Id==newCompanion.OrderId)).CustomerId==customers[1].Id&&
                (await Reload(legacyPrimary.Id)).ShippedAt==null&&(await Reload(newCompanion.Id)).ShippedAt==null,
                "reusing an old primary parcel preserves financial owners and never marks items shipped");
            await ShippingPackageService.CompleteAsync(db,legacyRequest,customers[0].Id);db.ChangeTracker.Clear();
            // Legacy contents may exist physically without an ERP order or even a customer profile.
            var unknown="legacy_"+stamp;var countCustomers=await db.Customers.CountAsync();var countInvoices=await db.Invoices.CountAsync();var countItems=await db.OrderItems.CountAsync();
            var unknownResolved=await service.ResolveCompanionAsync("https://t.me/"+unknown.ToUpperInvariant()+"/");
            check(unknownResolved.CustomerId is null&&unknownResolved.LegacyUsername==unknown&&await db.Customers.CountAsync()==countCustomers,
                "an unknown companion becomes a normalized annotation without creating a customer account");
            var noItemResolved=await service.ResolveCompanionAsync("@"+customers[1].Username);
            check(noItemResolved.CustomerId==customers[1].Id,"a registered customer without current ready items remains a valid shared companion");
            var annotationRoot=await Root(Guid.NewGuid(),805);var annotationRow=await db.NotificationOutbox.SingleAsync(n=>n.Id==annotationRoot);
            var annotationJson=System.Text.Json.Nodes.JsonNode.Parse(annotationRow.Payload)!;annotationJson["RequestId"]=null;annotationRow.Payload=annotationJson.ToJsonString();await db.SaveChangesAsync();db.ChangeTracker.Clear();
            await Select("start");await Input("@"+customers[0].Username);await Input("@"+customers[1].Username+"\n@"+unknown);
            check(transport.Texts.Any(t=>t.Contains("پست مشترک با:")&&t.Contains(unknown)&&t.Contains(customers[1].Username!))&&transport.Texts.Last().Contains("تأیید"),
                "the wizard previews known and unregistered legacy companions even when the whole parcel has zero ERP items");
            var annotationConfirm=transport.Buttons.Last(b=>b.StartsWith("shipping:shared:confirm:"));await Select(annotationConfirm["shipping:shared:".Length..]);db.ChangeTracker.Clear();
            var annotationContext=JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==annotationRoot)).Payload);
            var annotationRequest=annotationContext.RootElement.GetProperty("RequestId").GetGuid();requests.Add(annotationRequest);
            var annotationParcel=(await SharedShippingService.ReadAsync(db,annotationRequest))!;
            check(annotationParcel.CustomerIds.Order().SequenceEqual(new[]{customers[0].Id,customers[1].Id}.Order())&&annotationParcel.LegacyUsernames!.SequenceEqual([unknown])&&
                await db.Customers.CountAsync()==countCustomers&&await db.Invoices.CountAsync()==countInvoices&&await db.OrderItems.CountAsync()==countItems,
                "confirmed annotation-only sharing stores real member IDs and legacy usernames without fake orders, items, invoices or profiles");
            var annotationRefresh=await db.NotificationOutbox.SingleAsync(n=>n.Channel=="SharedShippingRefresh"&&n.Payload.Contains(annotationRoot.ToString()));
            await Call("RefreshSharedShippingAddressAsync",annotationRefresh,CancellationToken.None);
            check(transport.Edits.Last().Message==805&&transport.Edits.Last().Text.Contains("پست مشترک با:")&&transport.Edits.Last().Text.Contains("@"+unknown)&&transport.Edits.Last().Text.Contains("بدون آیتم"),
                "legacy companions are displayed on the original preparation address even with zero ready ERP rows");
            check((await ShippingPackageService.ValidatePackageAsync(db,annotationRequest,customers[0].Id)).Id==customers[0].Id,
                "an explicit annotation-only shared parcel has one verified physical address owner");
            var preparedLegacy=await TrackingPreparedAddresses.LoadAsync(db,"-100555001",default);
            check(preparedLegacy.Any(r=>r.RequestId==annotationRequest&&r.CustomerId==customers[0].Id),
                "annotation-only shared addresses remain eligible for exact tracking matching without invented invoice items");
            await Call("TryHandleShippingReferenceCallbackAsync",new TelegramCallbackQuery("legacy-shared",new(admin,null),new TelegramMessage(null,new(-100555001,"supergroup"),new(admin,null),null,MessageId:805),"shipping:refs:start:"+annotationRoot.ToString("N")),CancellationToken.None);
            check(await db.NotificationOutbox.AnyAsync(n=>n.Channel=="ShippingPackPhotoDraft"&&n.CustomerId==customers[0].Id&&n.Payload.Contains(annotationRequest.ToString())),
                "an empty legacy shared parcel retains the customer photo-confirmation entry point under its address");
            var legacyDraft=new TelegramShippingTrackingPhotoDraft{ChatId=-100555001,UserId=admin,CustomerId=customers[0].Id,ShippingRequestId=annotationRequest,SourceMessageId=805};
            await Call("QueueSharedTrackingPhotoAsync",new TelegramMessage(null,new(-100555001,"supergroup"),new(admin,null),null,MessageId:806),legacyDraft,"synthetic-legacy-tracking",CancellationToken.None);
            var legacyDispatch=await db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.ShippingRequestId==annotationRequest);batches.Add(legacyDispatch.ImportBatchId);operations.Add(legacyDispatch.Id);
            transport.Accepted.Clear();await Process(legacyDispatch);db.ChangeTracker.Clear();
            check(transport.Accepted.Order().SequenceEqual(groups.Take(2).Select(g=>g.ChatId).Order())&&(await SharedShippingService.ReadAsync(db,annotationRequest))!.ClosedAt!=null,
                "legacy shared tracking reaches every connected registered member and closes the zero-item parcel while annotation-only usernames never become guessed destinations");
            check(await db.OrderItems.CountAsync()==countItems&&await db.Invoices.CountAsync()==countInvoices,
                "tracking completion of legacy sharing does not manufacture or mark unrelated ERP items");
            var later=await Item(1,inventory:true);await ShippingPackageService.AttachInventoryAsync(db,customers[1].Id);db.ChangeTracker.Clear();
            check((await Reload(later.Id)).ShippingRequestId is null&&(await Reload(later.Id)).ShippedAt is null,
                "inventory created after a legacy shared parcel is closed cannot join or become shipped by replay");
            // A profile with no items and no linked group is also an annotation, not a reason to block primary dispatch.
            var noGroupRequest=Guid.NewGuid();var noGroupRoot=await Root(noGroupRequest,807);
            var noGroupPreview=await service.PreviewAsync(noGroupRequest,customers[0].Id,address.Id,[customers[2].Id],admin);
            await service.ConfirmAsync(noGroupPreview,admin,807,default,noGroupRoot);db.ChangeTracker.Clear();
            await db.CustomerTelegramGroups.Where(g=>g.Id==groups[2].Id).ExecuteUpdateAsync(q=>q.SetProperty(g=>g.IsActive,false));
            transport.Accepted.Clear();var noGroupDispatch=await Dispatch(noGroupRequest);await Process(noGroupDispatch);db.ChangeTracker.Clear();
            check(transport.Accepted.SequenceEqual([groups[0].ChatId])&&(await SharedShippingService.ReadAsync(db,noGroupRequest))!.ClosedAt!=null,
                "a companion with neither ERP items nor a connected group stays annotation-only and does not block the primary tracking delivery");
            await db.CustomerTelegramGroups.Where(g=>g.Id==groups[2].Id).ExecuteUpdateAsync(q=>q.SetProperty(g=>g.IsActive,true));
            var onlyLegacyRequest=Guid.NewGuid();var onlyLegacyRoot=await Root(onlyLegacyRequest,808);
            var onlyLegacyPreview=await service.PreviewAsync(onlyLegacyRequest,customers[0].Id,address.Id,[],admin,default,["only_"+stamp]);
            await service.ConfirmAsync(onlyLegacyPreview,admin,808,default,onlyLegacyRoot);db.ChangeTracker.Clear();
            check((await SharedShippingService.ReadAsync(db,onlyLegacyRequest))!.CustomerIds.SequenceEqual([customers[0].Id])&&onlyLegacyPreview.ItemIds.Length==0,
                "one registered primary plus an unregistered annotation is a valid parcel without any fabricated companion profile");
            var firstInventory=await Item(0,inventory:true);await ShippingPackageService.AttachInventoryAsync(db,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(firstInventory.Id)).ShippingRequestId==onlyLegacyRequest,
                "the first genuine ready inventory can join a previously empty shared parcel before dispatch");
            await ShippingPackageService.CompleteAsync(db,onlyLegacyRequest,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(firstInventory.Id)).ShippedAt!=null&&(await Reload(later.Id)).ShippedAt is null&&(await SharedShippingService.ReadAsync(db,onlyLegacyRequest))!.ClosedAt!=null,
                "completing legacy sharing updates only its newly added real inventory and never another companion's separate item");
            var duplicateLegacy=false;try{await service.PreviewAsync(Guid.NewGuid(),customers[0].Id,address.Id,[],admin,default,["missing_"+stamp,"MISSING_"+stamp]);}catch(InvalidOperationException){duplicateLegacy=true;}
            check(duplicateLegacy&&!db.ChangeTracker.HasChanges(),"case variants of the same annotation cannot create duplicate companions or any pending accounting writes");
            var ambiguousA=Guid.NewGuid();var ambiguousB=Guid.NewGuid();requests.AddRange([ambiguousA,ambiguousB]);
            await Item(0,parcel:ambiguousA);await Item(0,parcel:ambiguousB);
            var ambiguous=false;try{await service.ResolvePrimaryRequestAsync(customers[0].Id,null);}catch(InvalidOperationException){ambiguous=true;}
            check(ambiguous&&!db.ChangeTracker.HasChanges(),"an address-only selection never guesses or merges two existing primary parcels");
        }
        finally
        {
            db.ChangeTracker.Clear();var customerIds=customers.Select(c=>c.Id).ToArray();
            var dispatchIds=await db.TrackingDispatches.Where(d=>d.CustomerId!=null&&customerIds.Contains(d.CustomerId.Value)).Select(d=>d.Id).ToArrayAsync();
            var batchIds=await db.TrackingDispatches.Where(d=>dispatchIds.Contains(d.Id)).Select(d=>d.ImportBatchId).ToArrayAsync();
            await db.NotificationOutbox.Where(n=>customerIds.Contains(n.CustomerId)||operations.Contains(n.Id)||roots.Contains(n.Id)).ExecuteDeleteAsync();
            await db.TrackingDispatchDeliveries.Where(d=>dispatchIds.Contains(d.TrackingDispatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>dispatchIds.Contains(d.Id)).ExecuteDeleteAsync();
            await db.TrackingImportBatches.Where(b=>batchIds.Contains(b.Id)||batches.Contains(b.Id)).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.Addresses.Where(a=>a.Id==address.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>customerIds.Contains(g.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>customerIds.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}

public class SharedShippingSender:DispatchProxy
{
    public List<string> Texts=new(),Buttons=new(),Accepted=new();public List<(long Message,string Text)> Edits=new();public int AddressesSent;
    public string? RejectOnceChat;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;
        if(name=="AnswerCallbackAsync"&&args!.Length>1&&args[1] is string answer)Texts.Add(answer);
        if(name=="DownloadFileAsync")return Task.FromResult(new TelegramFileDownloadResult(true,[3,4,5]));
        if(name=="SendPhotoBytesWithKeyboardAsync")
        {
            var chat=(string)args![0]!;
            if(RejectOnceChat==chat){RejectOnceChat=null;return Task.FromResult(new TelegramSendResult(false,"429"){IsRetryable=true,RetryAfterSeconds=1});}
            Accepted.Add(chat);
        }
        if(name=="EditTextWithKeyboardAsync")Edits.Add(((long)args![1]!, (string)args[2]!));
        if(name is "SendAsync" or "SendInlineKeyboardAsync" or "SendKeyboardDismissAsync")
        {
            Texts.Add((string)args![1]!);if(((string)args[1]!).Contains("درخواست ارسال"))AddressesSent++;
        }
        foreach(var value in args??[])if(value is IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows)
            Buttons.AddRange(rows.SelectMany(r=>r).Select(b=>b.CallbackData??""));
        return Task.FromResult(new TelegramSendResult(true,MessageId:900));
    }
}
