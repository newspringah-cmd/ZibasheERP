using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class ShippingReferenceTests
{
    const long Operator=771234567,Ship=-10088112233,Client=-10088112234,Bot=881122;
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    public static async Task Run(Action<bool,string> check)
    {
        await Transport(check);
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="refs_"+Guid.NewGuid().ToString("N")[..12];var parcel=Guid.NewGuid();
        var customer=new Customer{Id=Guid.NewGuid(),FullName=marker,Username=marker,TelegramId="88119944",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var other=new Customer{Id=Guid.NewGuid(),FullName=marker+"other",Username=marker+"other",TelegramId="88119945",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        db.Customers.AddRange(customer,other);db.CustomerTelegramGroups.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=Client.ToString(),IsActive=true,Title=marker,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow});await db.SaveChangesAsync();
        var orders=new List<Guid>();var lists=new List<Guid>();var perfume=await db.Perfumes.AsNoTracking().FirstAsync();
        async Task<OrderItem> Item(Customer who,bool stock=false,Guid? request=null,int? stable=null)
        {
            var list=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(200000000,1000000000),StablePublicCode=stable,PersianName=marker+(stock?" موجودی":" عطر"),EnglishName=marker,
                PerfumeId=perfume.Id,TotalVolume=10,ReservedVolume=10,PricePerMl=100000,Status=SalesListStatus.Invoiced,OpenDate=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,IsInventoryOffer=stock,TelegramPhotoFileId="product-photo"};
            lists.Add(list.Id);db.SalesLists.Add(list);
            var order=new Order{Id=Guid.NewGuid(),CustomerId=who.Id,OrderNumber=marker+Guid.NewGuid().ToString("N"),IsInventory=stock,Source=OrderSource.SalesListInvoice,Status=OrderStatus.Invoiced,InvoiceIssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow};
            orders.Add(order.Id);db.Orders.Add(order);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,SalesListId=list.Id,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,RequestedVolumeMl=3,CreatedAt=DateTime.UtcNow,ShippingRequestId=request,ShippingRequestedAt=request.HasValue?DateTime.UtcNow:null};
            db.OrderItems.Add(item);db.Invoices.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber=marker+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Paid,TotalAmount=300000,IssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow});
            db.SalesListRequests.Add(new(){Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=who.TelegramId!,TelegramUsername=who.Username,VolumeMl=10,Status=SalesListRequestStatus.Invoiced,CreatedAt=DateTime.UtcNow,ConfirmedAt=DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddDays(1),ExternalReference=Guid.NewGuid().ToString("N")});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();return item;
        }
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingReferenceSender>();var sent=(ShippingReferenceSender)(object)sender;
        const long accountant=771234568;const string accountingChat="-10088112235";
        sent.AccountingChat=accountingChat;sent.AccountingAdmins.Add(accountant.ToString());
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=Operator.ToString(),AdminChatId=accountingChat,ShippingChatId=Ship.ToString(),BotToken=Bot+":fixture"}),
            ("_adminActivityContext",new ZibasheERP.Application.Services.AdminActivityContext()),
            ("_orderFlowDrafts",new TelegramOrderFlowDraftStore()),("_decantPhotoDrafts",new TelegramDecantPhotoDraftStore()),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(name,Private)!.SetValue(controller,value);
        async Task<object?> Call(string method,params object?[] args)
        {var task=(Task)typeof(TelegramWebhookController).GetMethod(method,Private)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        TelegramMessage Message(long chat=Ship,long id=600)=>new(null,new(chat,"supergroup"),new(Bot,null,IsBot:true),null,MessageId:id);
        TelegramCallbackQuery Callback(string action,Guid id,long actor=Operator,long chat=Ship,long message=600)=>new("fixture",new(actor,actor==88119944?customer.Username:null),Message(chat,message),"shipping:refs:"+action+":"+id.ToString("N"));
        Task<object?> Handle(string action,Guid id,long actor=Operator,long chat=Ship,long message=600)=>Call("TryHandleShippingReferenceCallbackAsync",Callback(action,id,actor,chat,message),CancellationToken.None);
        async Task<NotificationOutbox[]> Rows(string channel)=>await db.NotificationOutbox.AsNoTracking().Where(n=>n.CustomerId==customer.Id&&n.Channel==channel).OrderByDescending(n=>n.CreatedAt).ToArrayAsync();
        async Task<ShippingPackApprovalData> Receipt(Guid id)=>JsonSerializer.Deserialize<ShippingPackApprovalData>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id)).Payload)!;
        async Task SetReceipt(Guid id,ShippingPackApprovalData data)=>await db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,JsonSerializer.Serialize(data,(JsonSerializerOptions?)null)));
        async Task<bool> DispatchBlocked()=>await Throws(()=>ShippingPackageService.ValidateDispatchAsync(db,parcel,customer.Id));
        async Task DrainDecantReports()
        {
            for(var attempt=0;attempt<500;attempt++)
            {
                var job=(await Rows("ShippingDecantReferenceSend")).FirstOrDefault(n=>n.Status==NotificationOutboxStatus.Pending);
                if(job is null)return;
                var retry=(int?)await Call("ProcessShippingDecantStreamAsync",job,CancellationToken.None);
                if(!retry.HasValue)await db.NotificationOutbox.Where(n=>n.Id==job.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            }
            throw new InvalidOperationException("Decant report fixture did not finish");
        }
        try
        {
            var first=await Item(customer,request:parcel,stable:7007);var second=await Item(customer,true,parcel);var outside=await Item(other,stable:7007);
            await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"آدرس تست",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)parcel,null);
            var root=(await Rows("ShippingReferenceContext")).Single().Id;var address=sent.Calls.Last(x=>x.Method=="SendInlineKeyboardAsync");
            check(address.Buttons!.Count==4&&address.Buttons.SelectMany(x=>x).All(b=>System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!)<=64),"address has all four shipping buttons within Telegram callback length");
            check(address.Text.Contains("🔴 تعداد آیتم‌ها: 2")&&!address.Text.Contains(other.Username!),"address retains count footer and only the exact parcel's ready items");
            var messageId=address.Id;
            await Handle("photos",root,chat:Ship-999,message:messageId);check(sent.Photos.Count==0,"shipping reference token cannot be replayed in another chat");
            await Handle("photos",root,message:messageId+99);check(sent.Photos.Count==0,"shipping reference token binds to the exact source message");
            sent.Admin=false;await Handle("photos",root,actor:1999,message:messageId);check(sent.Photos.Count==0,"shipping photos reject unauthorized group participants");sent.Admin=true;
            void Outbox(string payload,DateTime at)=>db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,Channel="Telegram",EventType="DecantPhotoDelivery",Recipient=Client.ToString(),Payload=payload,Status=NotificationOutboxStatus.Processed,CreatedAt=at});
            Outbox(JsonSerializer.Serialize(new{SalesListId=first.SalesListId,FileId="decant-correct-cycle"}),DateTime.UtcNow.AddSeconds(-2));
            Outbox(JsonSerializer.Serialize(new{SalesListId=outside.SalesListId,FileId="other-cycle-same-code"}),DateTime.UtcNow);
            Outbox("{broken:"+first.SalesListId,DateTime.UtcNow.AddSeconds(1));await db.SaveChangesAsync();
            await Handle("photos",root,message:messageId);
            await DrainDecantReports();
            check(sent.Photos.Count==1&&sent.Photos[0].File=="decant-correct-cycle","decant photos use exact cycle, skip malformed history and never substitute product or reused-code photos");
            check(sent.Photos[0].Text.Contains("مقدار این مشتری: 3 میل"),"decant photo caption uses this recipient's ready-item quantity rather than the source list volume");
            check(sent.Calls.Any(c=>c.Text.Contains("عکس دکانت قابل تطبیق ثبت نشده")),"missing decant photos are reported explicitly while valid photos still display");
            await Handle("lists",root,message:messageId);var menu=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.StartsWith("📋"));
            check(menu.Buttons!.SelectMany(x=>x).Count()==1&&menu.Buttons!.SelectMany(x=>x).Single().Text.Contains("7007"),"completed-list menu includes billed ordinary cycle and excludes independent inventory");
            check(menu.Buttons!.SelectMany(x=>x).Single().Text.Contains(marker+" عطر | "+marker),"completed-list option displays both Persian and English perfume names on the same button");
            var listButton=menu.Buttons!.SelectMany(x=>x).Single();var child=Guid.ParseExact(listButton.CallbackData!.Split(':').Last(),"N");
            await Handle("list",child,message:menu.Id);check(sent.Calls.Any(c=>c.Text.StartsWith("✅ لیست فروش تکمیل شد")),"selected completed list is sent from its exact invoice cycle");
            var closePanel=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Buttons!.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:close:")==true));
            var viewId=Guid.ParseExact(closePanel.Buttons!.SelectMany(r=>r).Single().CallbackData!.Split(':').Last(),"N");
            var viewRow=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==viewId);
            var viewJson=JsonDocument.Parse(viewRow.Payload).RootElement;
            var resultIds=viewJson.GetProperty("MessageIds").EnumerateArray().Select(x=>x.GetInt64()).ToArray();
            var controlIds=viewJson.GetProperty("ControlMessageIds").EnumerateArray().Select(x=>x.GetInt64()).ToArray();
            sent.Admin=false;await Handle("close",viewId,actor:1999,message:closePanel.Id);sent.Admin=true;
            await Handle("close",viewId,message:messageId);
            check(sent.Deleted.Count==0,"report close rejects unauthorized actors and cannot target the address message");
            var blockedDelete=resultIds.First(id=>!controlIds.Contains(id));sent.FailDeleteId=blockedDelete;
            await Handle("close",viewId,message:closePanel.Id);
            check(!sent.Deleted.Any(controlIds.Contains)&&JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==viewId)).Payload).RootElement.GetProperty("State").GetString()=="closing",
                "partial report deletion retains close buttons and durable retry state");
            sent.FailDeleteId=null;await Handle("close",viewId,message:closePanel.Id);
            check(resultIds.All(sent.Deleted.Contains)&&!sent.Deleted.Contains(messageId)&&sent.Deleted.All(resultIds.Contains),"closing a multipage report deletes only its own exact photos, menu, list and control messages, preserving the address and other reports");
            var sentBeforeClosed=sent.Calls.Count(c=>c.Method.StartsWith("Send"));await Handle("list",child,message:menu.Id);
            check(sent.Calls.Count(c=>c.Method.StartsWith("Send"))==sentBeforeClosed,"closed report navigation cannot recreate clutter through stale list buttons");
            ((TelegramOrderFlowDraftStore)typeof(TelegramWebhookController).GetField("_orderFlowDrafts",Private)!.GetValue(controller)!).ToggleShippingSelection(Ship,Operator,parcel);
            await Call("SendSelectedShippingReportAsync",new TelegramCallbackQuery("fixture",new(Operator,null),Message(),"shipping:batchreport"),CancellationToken.None);
            var batchClose=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");var batchView=Guid.ParseExact(batchClose.Buttons!.SelectMany(r=>r).Single().CallbackData!.Split(':').Last(),"N");
            var batchIds=JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==batchView)).Payload).RootElement.GetProperty("MessageIds").EnumerateArray().Select(x=>x.GetInt64()).ToArray();
            await Handle("close",batchView,message:batchClose.Id);
            check(batchIds.Length>=3&&batchIds.All(sent.Deleted.Contains)&&!sent.Deleted.Contains(messageId),"aggregate selected-parcel report also closes all its text pages while preserving addresses");
            await ShippingPackageService.ValidateDispatchAsync(db,parcel,customer.Id);check(true,"legacy parcel without photo approval remains dispatchable");
            await Handle("start",root,message:messageId);var draft=(await Rows("ShippingPackPhotoDraft")).Single();
            var prompt=sent.Calls.Last(c=>c.Method=="SendKeyboardDismissAsync"&&c.Text.StartsWith("📷 بستهٔ"));
            async Task Upload(string file,long id,long actor=Operator,long? reply=null)=>await Call("TryHandleShippingPackPhotoMessageAsync",new TelegramMessage(null,new(Ship,"supergroup"),new(actor,null),null,
                Photo:[new(file,100,100)],MessageId:id,ReplyToMessage:Message(id:reply??prompt.Id)),CancellationToken.None);
            var baselinePhotos=sent.Photos.Count;await Upload("unbound",700,reply:prompt.Id+99);await Upload("not-my-draft",701,actor:777);
            check(JsonDocument.Parse((await Rows("ShippingPackPhotoDraft")).Single().Payload).RootElement.GetProperty("Files").ValueKind==JsonValueKind.Null,"unbound photo replies and another operator cannot attach to a parcel draft");
            check(!sent.Calls.Any(c=>c.Method=="SendForceReplyAsync"),"group pack-photo prompt dismisses old keyboard without creating another persistent ForceReply");
            var originalPrompt=prompt.Id;var deletedBeforeActive=sent.Deleted.Count;
            await Call("CleanupClosedShippingPackPromptsAsync",draft.Id,CancellationToken.None);
            check(sent.Deleted.Count==deletedBeforeActive,"prompt cleanup cannot delete an active pack-photo input form");
            await Upload("pack-1",702);await Upload("pack-1",702);
            var firstQuestion=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.Contains("آیا عکس دیگری دارید؟"));
            check(firstQuestion.Buttons!.First().Select(b=>b.Text).SequenceEqual(["بله","خیر"])&&sent.Calls.Count(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.Contains("آیا عکس دیگری دارید؟"))==1,
                "first uploaded photo shows exactly one Yes/No question and a replayed photo update cannot duplicate the question");
            await Handle("add",draft.Id,message:firstQuestion.Id);prompt=sent.Calls.Last(c=>c.Method=="SendKeyboardDismissAsync"&&c.Text.StartsWith("📷 بستهٔ"));
            var nextPromptCount=sent.Calls.Count(c=>c.Text.StartsWith("📷 بستهٔ"));await Handle("add",draft.Id,message:firstQuestion.Id);
            check(sent.Calls.Count(c=>c.Text.StartsWith("📷 بستهٔ"))==nextPromptCount,"repeated Yes click requests the next photo only once");
            await Upload("pack-2",703);
            var secondQuestion=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.Contains("آیا عکس دیگری دارید؟"));
            await Handle("add",draft.Id,message:secondQuestion.Id);prompt=sent.Calls.Last(c=>c.Method=="SendKeyboardDismissAsync"&&c.Text.StartsWith("📷 بستهٔ"));
            await Upload("pack-3",704);await Upload("pack-4",705);
            var finalQuestion=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.Contains("آیا عکس دیگری دارید؟"));
            check(finalQuestion.Buttons!.SelectMany(r=>r).Any(b=>b.Text=="خیر")&&!finalQuestion.Buttons!.SelectMany(r=>r).Any(b=>b.Text=="بله"),
                "at the existing three-photo limit the No choice sends the set without offering an impossible fourth photo");
            var beforePrematureCleanup=sent.Deleted.Count;await Handle("packclear",draft.Id,message:finalQuestion.Id);
            check(sent.Deleted.Count==beforePrematureCleanup,"cleanup cannot delete operator photos while the form is still collecting or awaiting No");
            var files=JsonDocument.Parse((await Rows("ShippingPackPhotoDraft")).Single().Payload).RootElement.GetProperty("Files");
            check(files.GetArrayLength()==3&&sent.Photos.Count==baselinePhotos,"photo collection deduplicates retries, caps at three and sends nothing to customer before operator confirmation");
            await Handle("send",draft.Id,message:finalQuestion.Id);await Handle("send",draft.Id,message:finalQuestion.Id);
            var jobs=await Rows(ShippingPackApproval.SendChannel);var receipt=(await Rows(ShippingPackApproval.ReceiptChannel)).Single();
            check(jobs.Length==1,"repeated send clicks create one durable photo-delivery job");
            check(!sent.Deleted.Contains(originalPrompt)&&!sent.Deleted.Contains(prompt.Id)&&!sent.Deleted.Contains(702),"No queues photos while preserving operator source photos and prompts until delivery succeeds and cleanup is explicitly chosen");
            check((await Receipt(receipt.Id)).PhotoDraftId==draft.Id,"No binds its durable delivery receipt to the exact operator photo form");
            check(sent.Calls.Single(c=>c.Text.StartsWith("✅ عکس‌ها در صف ارسال")).Method=="SendKeyboardDismissAsync","successful operator submission explicitly dismisses any legacy reply keyboard in its existing acknowledgement");
            check(!await DispatchBlocked(),"pending customer photo approval does not block tracking or dispatch");
            var sequence=0;sent.PhotoResult=()=>++sequence==2?new(false){IsRetryable=true,RetryAfterSeconds=2}:new(true,MessageId:++sent.NextId);
            var retry=(int?)await Call("ProcessShippingPackApprovalSendAsync",jobs[0],CancellationToken.None);
            check(retry==2&&(await Receipt(receipt.Id)).Deliveries.Count(d=>d.SentAt.HasValue)==1,"definite Telegram cooldown records first delivery and requests bounded retry");
            await Handle("packclear",draft.Id,message:finalQuestion.Id);
            check(!sent.Deleted.Contains(702)&&!sent.Calls.Any(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:packclear:")==true)==true),
                "partial customer delivery exposes no cleanup button and cannot erase the operator's photos");
            sent.PhotoResult=null;await Call("ProcessShippingPackApprovalSendAsync",jobs[0],CancellationToken.None);
            var delivered=await Receipt(receipt.Id);var deliveredPhotos=sent.Photos.Where(p=>p.Method=="SendPhotoOnceWithKeyboardAsync"&&p.Success&&p.Chat==Client.ToString()).ToArray();
            check(delivered.Deliveries.Count(d=>d.SentAt.HasValue)==3&&deliveredPhotos.Count(p=>p.File=="pack-1")==1,"resumed photo delivery skips known successful photos and sends remaining photos once");
            check(deliveredPhotos.Last().Text==ShippingPackApproval.Caption&&deliveredPhotos.All(p=>p.Chat==Client.ToString()),"customer receives exact requested caption and photos only in their connected accounting group");
            var cleanupPanel=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:packclear:")==true)==true);
            var cleanupState=JsonDocument.Parse((await Rows("ShippingPackPhotoDraft")).Single().Payload).RootElement;
            var expectedExtraIds=cleanupState.GetProperty("PromptIds").EnumerateArray().Concat(cleanupState.GetProperty("PhotoMessages").EnumerateArray())
                .Concat(cleanupState.GetProperty("UiMessageIds").EnumerateArray()).Select(v=>v.GetInt64()).Distinct().ToArray();
            var deletesBeforeCleanup=sent.Deleted.Count;
            sent.Admin=false;await Handle("packclear",draft.Id,actor:1999,message:cleanupPanel.Id);sent.Admin=true;
            await Handle("packclear",draft.Id,chat:Ship-1,message:cleanupPanel.Id);await Handle("packclear",draft.Id,message:messageId);
            check(sent.Deleted.Count==deletesBeforeCleanup,"cleanup button rejects unauthorized actors, another group and the original address as a forged source");
            var beforeFastCleanup=System.Text.Json.Nodes.JsonNode.Parse((await Rows("ShippingPackPhotoDraft")).Single().Payload)!;
            beforeFastCleanup["CleanupControlIds"]=new System.Text.Json.Nodes.JsonArray();
            beforeFastCleanup["UiMessageIds"]=new System.Text.Json.Nodes.JsonArray(beforeFastCleanup["UiMessageIds"]!.AsArray().Where(n=>n!.GetValue<long>()!=cleanupPanel.Id).Select(n=>n!.DeepClone()).ToArray());
            await db.NotificationOutbox.Where(n=>n.Id==draft.Id).ExecuteUpdateAsync(u=>u.SetProperty(n=>n.Payload,beforeFastCleanup.ToJsonString((JsonSerializerOptions?)null)));
            sent.FailDeleteId=703;
            var fastCleanup=Callback("packclear",draft.Id,message:cleanupPanel.Id) with{Message=Message(id:cleanupPanel.Id) with{Text=cleanupPanel.Text}};
            await Call("TryHandleShippingReferenceCallbackAsync",fastCleanup,CancellationToken.None);
            check(JsonDocument.Parse((await Rows("ShippingPackPhotoDraft")).Single().Payload).RootElement.GetProperty("CleanupControlIds").EnumerateArray().Any(v=>v.GetInt64()==cleanupPanel.Id),
                "a cleanup click arriving before the delivery notice receipt is bound is validated from the real bot message and records its exact message ID");
            check(!sent.Deleted.Contains(cleanupPanel.Id)&&!sent.Deleted.Contains(703),"failed operator-photo deletion preserves the cleanup control for retry");
            sent.FailDeleteId=null;await Handle("packclear",draft.Id,message:cleanupPanel.Id);
            check(expectedExtraIds.Where(mid=>mid!=cleanupPanel.Id).All(sent.Deleted.Contains)&&!sent.Deleted.Contains(cleanupPanel.Id)&&!sent.Deleted.Contains(messageId)&&!delivered.Deliveries.Any(d=>sent.Deleted.Contains(d.MessageId!.Value)),
                "successful cleanup removes exact wizard messages and operator photos, retaining only navigation, the address and customer-delivered photos");
            check(sent.Deleted.Skip(deletesBeforeCleanup).All(expectedExtraIds.Contains),"photo cleanup never guesses neighboring messages or removes unrelated group content");
            var cleanDeleteCount=sent.Deleted.Count;await Handle("packclear",draft.Id,message:cleanupPanel.Id);
            check(sent.Deleted.Count==cleanDeleteCount,"replayed cleanup does not delete any message twice");
            await Call("RecordShippingPackUiAsync",draft.Id,new TelegramSendResult(true,MessageId:90909),CancellationToken.None,false,false,0);
            check(sent.Deleted.Contains(90909),"late operator acknowledgement delivered after Cleanup is journaled and removed without leaving an orphan UI message");
            check((await Receipt(receipt.Id)).PhotoFileIds.SequenceEqual(["pack-1","pack-2","pack-3"])&&(await Receipt(receipt.Id)).Deliveries.All(d=>d.SentAt.HasValue),
                "cleanup preserves durable customer photo files and delivery history for subsequent customer confirmation and previous-shipment lookup");
            var photoMessage=delivered.Deliveries.Single(d=>d.Index==2).MessageId!.Value;
            await Handle("approve",receipt.Id,actor:1999,chat:Client,message:photoMessage);check((await Receipt(receipt.Id)).State!=ShippingPackApprovalState.Confirmed,"a customer-group participant or local admin cannot confirm another customer's package");
            await Handle("approve",receipt.Id,actor:88119944,chat:Client-1,message:photoMessage);check((await Receipt(receipt.Id)).State!=ShippingPackApprovalState.Confirmed,"customer cannot approve from an unrelated chat");
            await Handle("approve",receipt.Id,actor:88119944,chat:Client,message:photoMessage);await Handle("approve",receipt.Id,actor:88119944,chat:Client,message:photoMessage);
            var confirmationJob=(await Rows("ShippingPackCustomerResponse")).Single();
            await Call("ProcessShippingPackResponseAsync",confirmationJob,CancellationToken.None);await Call("ProcessShippingPackResponseAsync",confirmationJob,CancellationToken.None);
            check(sent.Calls.Count(c=>c.Method=="SendReplyOnceAsync"&&c.Text=="تایید شد ✅️"&&c.ReplyTo==photoMessage&&c.Chat==Client.ToString())==1,
                "customer approval creates one persistent visible confirmed reply under the actual package photo");
            check((await Receipt(receipt.Id)).State==ShippingPackApprovalState.Confirmed&&sent.Calls.Count(c=>c.Text.StartsWith("✅ تأیید مشتری برای ارسال"))==1,"customer confirmation is persistent, authorized and idempotent");
            foreach(var staff in new[]{Operator,accountant})
            {
                var staffId=Guid.NewGuid();var source=await Receipt(receipt.Id);
                var staffData=source with{State=ShippingPackApprovalState.AwaitingCustomer,ConfirmedAt=null,ConfirmedBy=null,ConfirmationResponseId=null};
                db.NotificationOutbox.Add(new(){Id=staffId,CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=parcel.ToString("N"),
                    Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddSeconds(1),Payload=JsonSerializer.Serialize(staffData)});await db.SaveChangesAsync();db.ChangeTracker.Clear();
                Guid? responseId=null;
                try
                {
                    await Handle("approve",staffId,actor:staff,chat:Client-1,message:photoMessage);
                    await Handle("approve",staffId,actor:staff,chat:Client,message:photoMessage+999);
                    check((await Receipt(staffId)).ConfirmedAt==null,"staff approval still rejects unrelated groups and forged photo message IDs");
                    await Handle("revise",staffId,actor:staff,chat:Client,message:photoMessage);
                    check((await Receipt(staffId)).State==ShippingPackApprovalState.AwaitingCustomer,"staff confirmation permission does not impersonate the customer's revision workflow");
                    await Handle("approve",staffId,actor:staff,chat:Client,message:photoMessage);
                    await Handle("approve",staffId,actor:88119944,chat:Client,message:photoMessage);
                    var accepted=await Receipt(staffId);responseId=accepted.ConfirmationResponseId;
                    check(accepted.State==ShippingPackApprovalState.Confirmed&&accepted.ConfirmedBy==staff&&responseId.HasValue,
                        "manager and registered accounting admin can confirm in the customer's group with their actual identity retained");
                    var response=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==responseId);
                    check(JsonDocument.Parse(response.Payload).RootElement.GetProperty("StaffApproval").GetBoolean(),"staff approval response is explicitly distinguished from customer confirmation");
                    var beforeStaff=sent.Calls.Count;
                    await Call("ProcessShippingPackResponseAsync",response,CancellationToken.None);await Call("ProcessShippingPackResponseAsync",response,CancellationToken.None);
                    var notices=sent.Calls.Skip(beforeStaff).ToArray();
                    check(notices.Count(c=>c.Method=="SendReplyOnceAsync"&&c.Chat==Client.ToString()&&c.ReplyTo==photoMessage&&c.Text.StartsWith("تایید شد ✅️\nثبت تأیید توسط "))==1&&
                        notices.Count(c=>c.Text.StartsWith("✅ تأیید مدیر/ادمین برای ارسال")&&c.Text.Contains(staff.ToString()))==1,
                        "staff approval sends exactly one attributed customer reply and one attributed preparation notice");
                }
                finally
                {
                    await db.NotificationOutbox.Where(n=>n.Id==staffId||(responseId.HasValue&&n.Id==responseId.Value)).ExecuteDeleteAsync();db.ChangeTracker.Clear();
                }
            }
            check(await db.OrderItems.AsNoTracking().AnyAsync(i=>i.Id==first.Id&&i.ShippedAt==null),"customer photo approval never marks items shipped");
            var snapshot=await Receipt(receipt.Id);await SetReceipt(receipt.Id,snapshot with{ItemIds=[first.Id]});check(!await DispatchBlocked(),"an outdated photo snapshot does not block a correctly owned current parcel");await SetReceipt(receipt.Id,snapshot);
            var photoCount=sent.Photos.Count;await Handle("last",root,message:messageId);check(sent.Photos.Count==photoCount&&sent.Calls.Any(c=>c.Text.Contains("هنوز عکس")),"previous-shipment history does not expose current unshipped photos");
            await ShippingPackageService.CompleteAsync(db,parcel,customer.Id);db.ChangeTracker.Clear();
            check((await Receipt(receipt.Id)).DispatchedAt.HasValue&&(await Receipt(receipt.Id)).State==ShippingPackApprovalState.Dispatched,"real dispatch atomically archives the approved package photos as shipment history");
            await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"بسته جدید",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,null,null);
            var nextRoot=(await Rows("ShippingReferenceContext")).First(n=>n.EventType=="AddressReferences");var nextAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
            photoCount=sent.Photos.Count;await Handle("last",nextRoot.Id,message:nextAddress.Id);
            check(sent.Photos.Count==photoCount+3&&sent.Photos.TakeLast(3).Select(p=>p.File).SequenceEqual(["pack-1","pack-2","pack-3"]),"new package can retrieve exactly the same customer's three photos from the previous dispatched package");
            // An ambiguous prior HTTP attempt must never be replayed after a restart.
            var newParcel=Guid.NewGuid();var newItem=await Item(customer,request:newParcel);var ambiguousId=Guid.NewGuid();
            var ambiguous=new ShippingPackApprovalData{CustomerId=customer.Id,RequestId=newParcel,ItemIds=[newItem.Id],PhotoFileIds=["ambiguous"],ShippingChatId=Ship.ToString(),OperatorUserId=Operator,State=ShippingPackApprovalState.Sending,Deliveries=[new(Client.ToString(),0,DateTime.UtcNow)]};
            db.NotificationOutbox.Add(new(){Id=ambiguousId,CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=newParcel.ToString("N"),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,Payload=JsonSerializer.Serialize(ambiguous)});await db.SaveChangesAsync();
            var ambiguousJob=new NotificationOutbox{CustomerId=customer.Id,Payload=JsonSerializer.Serialize(new{ApprovalId=ambiguousId})};photoCount=sent.Photos.Count;
            await Call("ProcessShippingPackApprovalSendAsync",ambiguousJob,CancellationToken.None);
            check(sent.Photos.Count==photoCount&&(await Receipt(ambiguousId)).State==ShippingPackApprovalState.NeedsReview,"interrupted unknown photo delivery is held for review instead of sending duplicate customer photos");
            var fastId=Guid.NewGuid();var fast=ambiguous with{PhotoFileIds=["fast-1","fast-2"],Deliveries=[new(Client.ToString(),0,DateTime.UtcNow,DateTime.UtcNow,990),new(Client.ToString(),1,DateTime.UtcNow)]};
            db.NotificationOutbox.Add(new(){Id=fastId,CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=newParcel.ToString("N"),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddSeconds(1),Payload=JsonSerializer.Serialize(fast)});await db.SaveChangesAsync();
            await Handle("approve",fastId,actor:88119944,chat:Client,message:991);
            check((await Receipt(fastId)).State==ShippingPackApprovalState.Confirmed&&(await Receipt(fastId)).Deliveries.Last().MessageId==991,"fast customer confirmation safely proves last photo delivery before the worker receives its HTTP response");
            photoCount=sent.Photos.Count;await Call("ProcessShippingPackApprovalSendAsync",new NotificationOutbox{CustomerId=customer.Id,Payload=JsonSerializer.Serialize(new{ApprovalId=fastId})},CancellationToken.None);
            check(sent.Photos.Count==photoCount&&(await Receipt(fastId)).State==ShippingPackApprovalState.Confirmed,"worker recovery preserves fast confirmation and never replays the two-photo package");
            var singleId=Guid.NewGuid();var single=ambiguous with{PhotoFileIds=["one-photo"],Deliveries=[]};
            db.NotificationOutbox.Add(new(){Id=singleId,CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=newParcel.ToString("N"),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddSeconds(2),Payload=JsonSerializer.Serialize(single)});await db.SaveChangesAsync();
            photoCount=sent.Photos.Count;await Call("ProcessShippingPackApprovalSendAsync",new NotificationOutbox{CustomerId=customer.Id,Payload=JsonSerializer.Serialize(new{ApprovalId=singleId})},CancellationToken.None);
            check(sent.Photos.Count==photoCount+1&&sent.Photos.Last().Text==ShippingPackApproval.Caption,"a single-photo package carries the complete customer caption and approval controls");
            await Handle("approve",fastId,actor:88119944,chat:Client,message:991);
            check((await Receipt(singleId)).State==ShippingPackApprovalState.AwaitingCustomer,"old photo approval cannot approve a newer replacement package proposal");
            var singleMessage=(await Receipt(singleId)).Deliveries.Single().MessageId!.Value;
            await Handle("revise",singleId,actor:88119944,chat:Client,message:singleMessage);
            check((await Receipt(singleId)).State==ShippingPackApprovalState.RevisionRequested&&!await Throws(()=>ShippingPackageService.ValidateDispatchAsync(db,newParcel,customer.Id)),"customer correction request persists as feedback without blocking operator tracking");
            var revisionPromptCount=sent.Calls.Count(c=>c.Text.StartsWith("✏️ لطفاً توضیح بده"));
            await Handle("revise",singleId,actor:88119944,chat:Client,message:singleMessage);
            check(sent.Calls.Count(c=>c.Text.StartsWith("✏️ لطفاً توضیح بده"))==revisionPromptCount&&(await Rows("ShippingPackRevisionDraft")).Length==1,
                "repeated Needs correction click reuses the same durable input form rather than duplicating prompts");
            var revisionDraft=(await Rows("ShippingPackRevisionDraft")).Single();
            var revisionData=JsonDocument.Parse(revisionDraft.Payload).RootElement;var revisionPrompt=revisionData.GetProperty("PromptId").GetInt64();
            var responseCount=(await Rows("ShippingPackCustomerResponse")).Length;
            async Task<object?> RevisionMessage(string text,long message,long actor=88119944,long chat=Client,long? promptId=null)=>await Call("TryHandleShippingPackRevisionMessageAsync",
                new TelegramMessage(text,new(chat,"supergroup"),new(actor,actor==88119944?customer.Username:null),null,MessageId:message,ReplyToMessage:Message(chat,promptId??revisionPrompt)),CancellationToken.None);
            check(!(bool)(await RevisionMessage("توضیح فرد دیگر",1200,Operator))!&&!(bool)(await RevisionMessage("گروه دیگر",1201,chat:Client-9))!&&
                !(bool)(await RevisionMessage("پیام معمولی",1202,promptId:revisionPrompt+99))!,"revision input rejects other people, unrelated groups and ordinary replies instead of capturing customer conversation");
            await RevisionMessage(new string('x',1501),1203);
            check(JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==revisionDraft.Id)).Payload).RootElement.GetProperty("State").GetString()=="text",
                "oversized correction text does not enter the review queue");
            var originalController=controller;var restarted=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach(var fieldInfo in typeof(TelegramWebhookController).GetFields(Private))if(fieldInfo.GetValue(controller) is {} fieldValue)fieldInfo.SetValue(restarted,fieldValue);
            controller=restarted;
            await RevisionMessage("شیشهٔ عطر دوم نیاز به تغییر دارد",1204);controller=originalController;
            var revisionPreview=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.StartsWith("✏️ توضیحات اصلاح بسته:"));
            var previewCount=sent.Calls.Count(c=>c.Text.StartsWith("✏️ توضیحات اصلاح بسته:"));await RevisionMessage("شیشهٔ عطر دوم نیاز به تغییر دارد",1204);
            check(sent.Calls.Count(c=>c.Text.StartsWith("✏️ توضیحات اصلاح بسته:"))==previewCount,"replayed correction text does not duplicate the preview or notify the operator");
            check((await Rows("ShippingPackCustomerResponse")).Length==responseCount&&revisionPreview.Buttons!.SelectMany(r=>r).All(b=>System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!)<=64),
                "revision draft survives controller restart and shows a bounded Send/Edit/Cancel preview without notifying the operator before Send");
            await Handle("fixsend",revisionDraft.Id,actor:Operator,chat:Client,message:revisionPreview.Id);
            await Handle("fixsend",revisionDraft.Id,actor:88119944,chat:Client,message:revisionPreview.Id+99);
            check((await Rows("ShippingPackCustomerResponse")).Length==responseCount,"revision submission rejects unauthorized people and a forged preview message");
            await Handle("fixedit",revisionDraft.Id,actor:88119944,chat:Client,message:revisionPreview.Id);
            const string correctedNote="شیشهٔ عطر دوم اشتباه است؛ لطفاً شیشهٔ فانتزی آماده کنید. <متن مشتری>";
            await RevisionMessage(correctedNote,1205);
            var correctedPreview=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.StartsWith("✏️ توضیحات اصلاح بسته:"));
            await Handle("fixsend",revisionDraft.Id,actor:88119944,chat:Client,message:revisionPreview.Id);
            check((await Rows("ShippingPackCustomerResponse")).Length==responseCount,"old preview buttons cannot send a subsequently edited correction");
            await Handle("fixsend",revisionDraft.Id,actor:88119944,chat:Client,message:correctedPreview.Id);
            await Handle("fixsend",revisionDraft.Id,actor:88119944,chat:Client,message:correctedPreview.Id);
            var reviewJob=(await Rows("ShippingPackCustomerResponse")).Single(n=>n.EventType=="revision");
            check((await Rows("ShippingPackCustomerResponse")).Length==responseCount+1&&(await Receipt(singleId)).RevisionText==correctedNote&&
                (await Receipt(singleId)).RevisionSubmittedAt.HasValue,"explicit Send stores the exact edited correction and queues one durable review despite repeated clicks");
            await db.OrderItems.Where(i=>i.Id==newItem.Id).ExecuteUpdateAsync(u=>u.SetProperty(i=>i.ShippingTelegramMessageId,(long?)12000));
            var correctionBeforeDispatch=await Receipt(singleId);
            await ShippingPackApproval.MarkDispatchedAsync(db,customer.Id,newParcel,[newItem.Id]);
            sent.TextResult=(chat,text)=>chat==Ship.ToString()&&text.StartsWith("✏️ درخواست اصلاح")?new(false){IsRetryable=true,RetryAfterSeconds=1}:new(true,MessageId:++sent.NextId);
            var reviewRetry=(int?)await Call("ProcessShippingPackResponseAsync",reviewJob,CancellationToken.None);
            check(reviewRetry==1&&!sent.Calls.Any(c=>c.Text=="برای بررسی مجدد ارسال شد"),"customer review-sent reply waits until the operator's actual note is delivered, including Telegram cooldown");
            sent.TextResult=(chat,text)=>text=="برای بررسی مجدد ارسال شد"?new(false){IsRetryable=true,RetryAfterSeconds=1}:new(true,MessageId:++sent.NextId);
            await Call("ProcessShippingPackResponseAsync",reviewJob,CancellationToken.None);
            sent.TextResult=null;await Call("ProcessShippingPackResponseAsync",reviewJob,CancellationToken.None);await Call("ProcessShippingPackResponseAsync",reviewJob,CancellationToken.None);
            check((await Receipt(singleId)).State==ShippingPackApprovalState.Dispatched&&(await Receipt(singleId)).RevisionText==correctedNote,
                "operator dispatch preserves a queued customer correction and its delivery rather than losing feedback");
            await SetReceipt(singleId,correctionBeforeDispatch);
            check(sent.Calls.Count(c=>c.Success&&c.Chat==Ship.ToString()&&c.Text.StartsWith("✏️ درخواست اصلاح")&&c.Text.Contains(correctedNote))==1&&
                sent.Calls.Count(c=>c.Success&&c.Chat==Client.ToString()&&c.Text=="برای بررسی مجدد ارسال شد"&&c.ReplyTo==singleMessage)==1,
                "operator receives the exact correction once and customer gets one visible review-sent reply under their photo, without resending the operator note on retry");
            check(sent.Calls.Single(c=>c.Success&&c.Chat==Ship.ToString()&&c.Text.StartsWith("✏️ درخواست اصلاح")&&c.Text.Contains(correctedNote)).ReplyTo==12000,
                "correction note is threaded to the exact recorded package address rather than another customer's photo or address");
            await Handle("approve",singleId,actor:88119944,chat:Client,message:singleMessage);
            check((await Receipt(singleId)).State==ShippingPackApprovalState.RevisionRequested&&!await Throws(()=>ShippingPackageService.ValidateDispatchAsync(db,newParcel,customer.Id)),
                "old-photo approval cannot erase submitted correction feedback and operator tracking stays available");
            var textConfirm=new TelegramMessage("تایید",new(Client,"supergroup"),new(88119944,customer.Username),null,MessageId:1300,ReplyToMessage:Message(Client,singleMessage));
            check((bool)(await Call("TryHandleShippingPackPhotoMessageAsync",textConfirm,CancellationToken.None))!&&(await Receipt(singleId)).State==ShippingPackApprovalState.RevisionRequested,
                "text confirmation of a photo awaiting correction returns a controlled explanation instead of bypassing review or failing the webhook");
            var cancelApprovalId=Guid.NewGuid();var cancelProposal=single with{Deliveries=[new(Client.ToString(),0,DateTime.UtcNow,DateTime.UtcNow,2002)]};
            db.NotificationOutbox.Add(new(){Id=cancelApprovalId,CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=newParcel.ToString("N"),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow.AddSeconds(3),Payload=JsonSerializer.Serialize(cancelProposal)});await db.SaveChangesAsync();
            await Handle("revise",cancelApprovalId,actor:88119944,chat:Client,message:2002);
            var cancelRevision=(await Rows("ShippingPackRevisionDraft")).First();var cancelRevisionPrompt=JsonDocument.Parse(cancelRevision.Payload).RootElement.GetProperty("PromptId").GetInt64();
            await RevisionMessage("/cancel",1206,promptId:cancelRevisionPrompt);
            check((await Receipt(cancelApprovalId)).State==ShippingPackApprovalState.AwaitingCustomer&&!sent.Calls.Any(c=>c.Method=="SendForceReplyAsync"),
                "cancelling correction input leaves the parcel awaiting explicit approval and never opens a sticky ForceReply");
            await Handle("approve",cancelApprovalId,actor:88119944,chat:Client,message:2002);
            var uncertainJob=(await Rows("ShippingPackCustomerResponse")).First(n=>n.EventType=="confirmed");
            sent.TextResult=(chat,text)=>text=="تایید شد ✅️"?new(false,"unknown HTTP outcome"):new(true,MessageId:++sent.NextId);
            await Call("ProcessShippingPackResponseAsync",uncertainJob,CancellationToken.None);var uncertainAttempts=sent.Calls.Count(c=>c.Text=="تایید شد ✅️");
            await Call("ProcessShippingPackResponseAsync",uncertainJob,CancellationToken.None);sent.TextResult=null;
            check(sent.Calls.Count(c=>c.Text=="تایید شد ✅️")==uncertainAttempts,"an interrupted unknown customer confirmation delivery is journaled and never duplicated automatically");
            var trackingStore=(TelegramOrderFlowDraftStore)typeof(TelegramWebhookController).GetField("_orderFlowDrafts",Private)!.GetValue(controller)!;
            foreach(var mode in new[]{"tracking","trackingbatch","trackingcustomer"})
            {
                var trackingParcel=Guid.NewGuid();var trackingItem=await Item(customer,request:trackingParcel);
                await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"بسته تست تغییر مسیر عکس",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)trackingParcel,null);
                var trackingRoot=(await Rows("ShippingReferenceContext")).First(n=>n.EventType=="AddressReferences").Id;
                var trackingAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
                await Handle("start",trackingRoot,message:trackingAddress.Id);
                var pending=(await Rows("ShippingPackPhotoDraft")).First(n=>n.Status==NotificationOutboxStatus.Pending);
                var otherPayload=System.Text.Json.Nodes.JsonNode.Parse(pending.Payload)!;otherPayload["UserId"]=777001L;
                var anotherOperator=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=anotherOperator,CustomerId=customer.Id,Channel="ShippingPackPhotoDraft",EventType="AwaitingPhotos",Recipient=Ship.ToString(),Payload=otherPayload.ToJsonString(),Status=NotificationOutboxStatus.Pending,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
                var trackingData="shipping:"+mode+":"+(mode=="trackingcustomer"?customer.Id:trackingParcel).ToString("N");
                if(mode!="trackingcustomer")
                {
                    var waiting=new ShippingPackApprovalData{CustomerId=customer.Id,RequestId=trackingParcel,ItemIds=[trackingItem.Id],State=ShippingPackApprovalState.AwaitingCustomer,ShippingChatId=Ship.ToString(),OperatorUserId=Operator};
                    db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=trackingParcel.ToString("N"),Payload=JsonSerializer.Serialize(waiting),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
                }
                await Call("TryHandleShippingRequestCallbackAsync",new TelegramCallbackQuery("fixture",new(Operator,null),Message(id:trackingAddress.Id),trackingData),CancellationToken.None);
                var cancelled=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==pending.Id);
                check(cancelled.Status==NotificationOutboxStatus.Processed&&JsonDocument.Parse(cancelled.Payload).RootElement.GetProperty("State").GetString()=="cancelled"&&
                    (await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==anotherOperator)).Status==NotificationOutboxStatus.Pending,
                    mode+" button cancels only this operator's pending pack-photo form, preserving other operators");
                check(trackingStore.TryGetShippingTrackingPhoto(Ship,Operator,out var activeTracking)&&activeTracking.ShippingRequestId==trackingParcel,
                    mode+" button selects the correct tracking photo destination");
                // Reproduce a durable stale form surviving from the previous version.
                await db.NotificationOutbox.Where(n=>n.Id==pending.Id).ExecuteUpdateAsync(u=>u.SetProperty(n=>n.Status,NotificationOutboxStatus.Pending).SetProperty(n=>n.Payload,pending.Payload));
                var trackingPhoto=new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,Photo:[new("tracking-mode-"+mode,100,100)],MessageId:1600);
                var beforeTracking=sent.Photos.Count;var beforeReplyErrors=sent.Calls.Count(c=>c.Text.Contains("برای افزودن عکس بسته"));
                check(!(bool)(await Call("TryHandleShippingPackPhotoMessageAsync",trackingPhoto,CancellationToken.None))!&&sent.Calls.Count(c=>c.Text.Contains("برای افزودن عکس بسته"))==beforeReplyErrors,
                    mode+" photo bypasses and closes a stale pack draft instead of demanding a reply to the pack prompt");
                if(mode!="trackingcustomer")
                {
                    check((bool)(await Call("TryHandleShippingTrackingPhotoMessageAsync",trackingPhoto,CancellationToken.None))!&&sent.Photos.Count==beforeTracking+1&&
                        sent.Photos.Last().Chat==Client.ToString()&&sent.Photos.Last().Text=="📦 تصویر کد رهگیری مرسوله شما",
                        mode+" photo reaches the real tracking handler and only the correct customer, without pack-photo confirmation text");
                    check(await db.OrderItems.AsNoTracking().AnyAsync(i=>i.Id==trackingItem.Id&&i.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped),mode+" tracking completion retains the existing correct parcel dispatch behavior");
                    check((await Rows(ShippingTrackingNotices.Channel)).Any(n=>JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(n.Payload)!.SourceMessageId==trackingAddress.Id),
                        mode+" successful photo queues its success notice under the original address");
                }
                else
                {
                    // Switching back is symmetric: starting pack photos clears tracking.
                    await Handle("start",trackingRoot,message:trackingAddress.Id);
                    check(!trackingStore.TryGetShippingTrackingPhoto(Ship,Operator,out _),"switching back to pack approval clears tracking mode rather than mixing the two photo types");
                    await Call("CancelPendingShippingPackDraftsAsync",Ship,Operator,CancellationToken.None);
                }
            }
            var manualAddress=new Address{Id=Guid.NewGuid(),CustomerId=other.Id,ReceiverName="Synthetic manual recipient",Mobile="09120000000",City="تهران",FullAddress="Synthetic only",PostalCode="0123456789",RegistrationTelegramChatId="1907823600",CreatedAt=DateTime.UtcNow};
            await db.Customers.Where(c=>c.Id==other.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Notes,"manual-address-registration:1907823600:"+other.Id.ToString("N")));
            db.Addresses.Add(manualAddress);await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var resolved=(string[])(await Call("ResolveTrackingDestinationChatIdsAsync",new TrackingDispatch{Id=Guid.NewGuid(),CustomerId=other.Id},CancellationToken.None))!;
            check(resolved.SequenceEqual([Operator.ToString()]),"PDF tracking for an explicitly manual registration targets only the configured owner rather than the registrar");
            await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"Synthetic manual address",other,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,null,manualAddress.Id);
            var manualSource=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync").Id;
            trackingStore.SetShippingTrackingPhoto(new(){ChatId=Ship,UserId=Operator,CustomerId=other.Id,ShippingRequestId=Guid.Empty,SourceMessageId=manualSource});
            var manualPhoto=new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,Photo:[new("manual-registration-tracking",100,100)],MessageId:1700);
            var beforeManualPhotos=sent.Photos.Count;
            await Call("TryHandleShippingTrackingPhotoMessageAsync",manualPhoto,CancellationToken.None);
            check(sent.Photos.Count==beforeManualPhotos+1&&sent.Photos.Last().Chat==Operator.ToString(),"the real manual tracking-photo handler sends to the owner even when another admin registered the address");
            check((await db.NotificationOutbox.AsNoTracking().Where(n=>n.CustomerId==other.Id&&n.Channel==ShippingTrackingNotices.Channel).ToArrayAsync()).Any(n=>JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(n.Payload)!.SourceMessageId==manualSource),
                "manual-registration delivery to the manager still marks its own preparation address rather than the manager's chat");
            await db.Customers.Where(c=>c.Id==other.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Notes,(string?)null));
            var normalResolved=(string[])(await Call("ResolveTrackingDestinationChatIdsAsync",new TrackingDispatch{Id=Guid.NewGuid(),CustomerId=other.Id},CancellationToken.None))!;
            check(normalResolved.SequenceEqual(["1907823600"]),"ordinary address-only customers retain their original tracking destination");
            await db.Addresses.Where(a=>a.Id==manualAddress.Id).ExecuteDeleteAsync();
            await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"لغو فرم عکس",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)newParcel,null);
            var cancelRoot=(await Rows("ShippingReferenceContext")).First(n=>n.EventType=="AddressReferences").Id;var cancelAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
            foreach(var textCancel in new[]{false,true})
            {
                await Handle("start",cancelRoot,message:cancelAddress.Id);
                var cancelDraft=(await Rows("ShippingPackPhotoDraft")).First(n=>n.Status==NotificationOutboxStatus.Pending);
                var cancelPrompt=sent.Calls.Last(c=>c.Method=="SendKeyboardDismissAsync"&&c.Text.StartsWith("📷 بستهٔ"));
                if(textCancel)await Call("TryHandleShippingPackPhotoMessageAsync",new TelegramMessage("/cancel",new(Ship,"supergroup"),new(Operator,null),null),CancellationToken.None);
                else await Handle("cancel",cancelDraft.Id,message:cancelPrompt.Id);
                check(sent.Deleted.Contains(cancelPrompt.Id)&&sent.Calls.Last().Method=="SendKeyboardDismissAsync",(textCancel?"text cancellation":"cancel button")+" removes only its own photo prompt and closes the group reply state");
            }
            foreach(var setSize in new[]{1,2})
            {
                var simpleParcel=Guid.NewGuid();await Item(customer,request:simpleParcel);
                await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"بسته تست بله و خیر",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)simpleParcel,null);
                var simpleRoot=(await Rows("ShippingReferenceContext")).First(n=>n.EventType=="AddressReferences").Id;var simpleAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
                var skippedParcel=Guid.NewGuid();var skippedItem=await Item(other,request:skippedParcel);
                await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"بسته قبلاً ارسال شده",other,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)skippedParcel,null);
                await db.OrderItems.Where(x=>x.Id==skippedItem.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ShippedAt,DateTime.UtcNow).SetProperty(x=>x.FulfillmentStatus,OrderItemFulfillmentStatus.Shipped));
                var nextParcel=Guid.NewGuid();await Item(other,request:nextParcel);
                await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"آدرس بعدی آماده‌سازی",other,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)nextParcel,null);
                var nextReadyAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
                await Handle("start",simpleRoot,message:simpleAddress.Id);
                var simpleDraft=(await Rows("ShippingPackPhotoDraft")).First(n=>n.Status==NotificationOutboxStatus.Pending);
                var stalePhoto=new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,
                    Photo:[new("stale-unbound-photo",100,100)],MessageId:10900+setSize,Date:DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());
                await Call("TryHandleShippingPackPhotoMessageAsync",stalePhoto,CancellationToken.None);
                check(JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==simpleDraft.Id)).Payload).RootElement.GetProperty("Files").ValueKind==JsonValueKind.Null,
                    "delayed unbound photo from before the form opened cannot attach to a new parcel");
                var ambiguousPhotoDraftId=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=ambiguousPhotoDraftId,CustomerId=customer.Id,Channel="ShippingPackPhotoDraft",EventType="AwaitingPhotos",Recipient=Ship.ToString(),Payload=simpleDraft.Payload,Status=NotificationOutboxStatus.Pending,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
                await Call("TryHandleShippingPackPhotoMessageAsync",stalePhoto with{Date=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),MessageId=10910+setSize},CancellationToken.None);
                check((await Rows("ShippingPackPhotoDraft")).Where(n=>n.Status==NotificationOutboxStatus.Pending).All(n=>JsonDocument.Parse(n.Payload).RootElement.GetProperty("Files").ValueKind==JsonValueKind.Null),
                    "an unbound upload never chooses between multiple pending forms for the same operator");
                await db.NotificationOutbox.Where(n=>n.Id==ambiguousPhotoDraftId).ExecuteDeleteAsync();
                ShippingReferenceCall simpleQuestion=null!;var uploaded=new List<long>();
                for(var n=0;n<setSize;n++)
                {
                    var inputPrompt=sent.Calls.Last(c=>c.Method=="SendKeyboardDismissAsync"&&c.Text.StartsWith("📷 بستهٔ"));var uploadId=11000+setSize*10+n;uploaded.Add(uploadId);
                    await Call("TryHandleShippingPackPhotoMessageAsync",new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,
                        Photo:[new("yes-no-"+setSize+"-"+n,100,100)],MessageId:uploadId,Date:DateTimeOffset.UtcNow.ToUnixTimeSeconds()),CancellationToken.None);
                    simpleQuestion=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.Contains("آیا عکس دیگری دارید؟"));
                    check(JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(x=>x.Id==simpleDraft.Id)).Payload).RootElement.GetProperty("Files").GetArrayLength()==n+1,
                        "direct upload without Reply reaches the exact operator form on first and additional photo");
                    if(n+1<setSize)await Handle("add",simpleDraft.Id,message:simpleQuestion.Id);
                }
                await Handle("send",simpleDraft.Id,message:simpleQuestion.Id);
                var simpleApproval=JsonDocument.Parse(simpleDraft.Payload).RootElement.GetProperty("ApprovalId").GetGuid();
                var simpleJob=(await Rows(ShippingPackApproval.SendChannel)).Single(n=>JsonDocument.Parse(n.Payload).RootElement.GetProperty("ApprovalId").GetGuid()==simpleApproval);
                await Call("ProcessShippingPackApprovalSendAsync",simpleJob,CancellationToken.None);
                var simpleReceipt=await Receipt(simpleApproval);var simpleCleanup=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:refs:packclear:"+simpleDraft.Id.ToString("N"))==true);
                var currentAddressUrl=$"https://t.me/c/{Ship.ToString()[4..]}/{simpleAddress.Id}";
                var nextReadyAddressUrl=$"https://t.me/c/{Ship.ToString()[4..]}/{nextReadyAddress.Id}";
                check(simpleCleanup.Buttons!.SelectMany(r=>r).Any(b=>b.Url==currentAddressUrl)&&simpleCleanup.Buttons!.SelectMany(r=>r).Any(b=>b.Url==nextReadyAddressUrl),
                    "delivery result links directly to its original address and next ready address, skipping a shipped parcel");
                check(simpleReceipt.PhotoFileIds.Length==setSize&&simpleReceipt.Deliveries.Count(d=>d.SentAt.HasValue)==setSize,
                    setSize+"-photo Yes/No flow sends exactly the collected set automatically on No");
                await Handle("packclear",simpleDraft.Id,message:simpleCleanup.Id);
                check(uploaded.All(sent.Deleted.Contains)&&!sent.Deleted.Contains(simpleAddress.Id)&&simpleReceipt.Deliveries.All(d=>!sent.Deleted.Contains(d.MessageId!.Value)),
                    setSize+"-photo cleanup deletes only operator extras and retains customer photos and parcel address");
                var compactNavigation=sent.Calls.Last(c=>c.Id==simpleCleanup.Id&&c.Method=="EditTextWithKeyboardAsync");
                check(!sent.Deleted.Contains(simpleCleanup.Id)&&compactNavigation.Text=="✅ پیام‌های اضافی این بسته پاک شد."&&
                    compactNavigation.Buttons!.SelectMany(r=>r).Any(b=>b.Url==currentAddressUrl)&&compactNavigation.Buttons!.SelectMany(r=>r).All(b=>b.CallbackData is null),
                    "cleanup leaves one compact address-navigation panel in place without another message or stale cleanup button");
                if(setSize==1)
                {
                    await Item(customer,request:simpleParcel);
                    await Handle("start",simpleRoot,message:simpleAddress.Id);
                    var appendDraft=(await Rows("ShippingPackPhotoDraft")).First(n=>n.Status==NotificationOutboxStatus.Pending);
                    var appendState=JsonDocument.Parse(appendDraft.Payload).RootElement;
                    check(appendState.GetProperty("ExistingPhotoCount").GetInt32()==1&&appendState.GetProperty("PreviousApprovalId").GetGuid()==simpleApproval,
                        "adding ready inventory to the same ERP parcel preserves its first photo as the continuation base");
                    var beforeAppend=sent.Photos.Count;
                    await Call("TryHandleShippingPackPhotoMessageAsync",new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,
                        Photo:[new("erp-parcel-append",100,100)],MessageId:11999,Date:DateTimeOffset.UtcNow.ToUnixTimeSeconds()),CancellationToken.None);
                    var appendQuestion=sent.Calls.Last(c=>c.Text.Contains("آیا عکس دیگری دارید؟"));await Handle("send",appendDraft.Id,message:appendQuestion.Id);
                    var appendApproval=appendState.GetProperty("ApprovalId").GetGuid();var appendJob=(await Rows(ShippingPackApproval.SendChannel)).Single(n=>JsonDocument.Parse(n.Payload).RootElement.GetProperty("ApprovalId").GetGuid()==appendApproval);
                    await Call("ProcessShippingPackApprovalSendAsync",appendJob,CancellationToken.None);
                    var appended=await Receipt(appendApproval);
                    check(appended.ItemIds.Length==2&&appended.PhotoFileIds.Length==2&&sent.Photos.Count==beforeAppend+1&&sent.Photos.Last().ReplyTo==simpleReceipt.Deliveries.Single().MessageId,
                        "a later ERP parcel photo includes newly added ready items without resending or losing the first photo");
                }
            }
            var streamParcel=Guid.NewGuid();var streamItems=new List<OrderItem>();
            for(var n=0;n<14;n++)
            {
                var entry=await Item(customer,n==13,streamParcel);streamItems.Add(entry);
                if(n==13)await db.OrderItems.Where(x=>x.Id==entry.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RequestedVolumeMl,1.5m));
                Outbox(JsonSerializer.Serialize(new{SalesListId=entry.SalesListId,FileId="stream-decant-"+(n==13?0:n)}),DateTime.UtcNow);
            }
            await db.SaveChangesAsync();
            await Call("SendAddressWithReadyItemsAsync",Ship.ToString(),"آدرس گزارش همه عکس‌ها",customer,Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(),CancellationToken.None,(Guid?)streamParcel,null);
            var streamRoot=(await Rows("ShippingReferenceContext")).First(n=>n.EventType=="AddressReferences").Id;var streamAddress=sent.Calls.Last(c=>c.Method=="SendInlineKeyboardAsync");
            var beforeStreamPhotos=sent.Photos.Count;await Handle("photos",streamRoot,message:streamAddress.Id);
            var streamJob=(await Rows("ShippingDecantReferenceSend")).First(n=>n.Status==NotificationOutboxStatus.Pending);
            var streamClose=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:close:")==true)==true);
            var streamView=Guid.ParseExact(streamClose.Buttons!.SelectMany(r=>r).Single().CallbackData!.Split(':').Last(),"N");
            check(sent.Photos.Count==beforeStreamPhotos,"large decant report queues immediately instead of holding the Telegram webhook open for all photos");
            sent.PhotoResult=()=>new(false,"cooldown"){IsRetryable=true,RetryAfterSeconds=7};
            var streamRetry=(int?)await Call("ProcessShippingDecantStreamAsync",streamJob,CancellationToken.None);
            check(streamRetry==7&&JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==streamJob.Id)).Payload).RootElement.GetProperty("Next").GetInt32()==0,
                "Telegram cooldown preserves the current decant photo and schedules automatic continuation without another click");
            sent.PhotoResult=null;await DrainDecantReports();
            var streamed=sent.Photos.Skip(beforeStreamPhotos).Where(p=>p.Success).ToArray();
            check(streamed.Length==13&&streamed.Select(p=>p.File).SequenceEqual(Enumerable.Range(0,13).Select(n=>"stream-decant-"+n)),
                "one address-button click streams every matched decant photo in order beyond ten with no manual continuation page");
            check(streamed[0].Text.Contains("مقدار این مشتری: 3 میل")&&streamed[0].Text.Contains("مقدار این مشتری: 1.5 میل")&&streamed.All(p=>p.Text.Contains("مقدار این مشتری:")),
                "shared decant photo captions retain separate perfume quantities including exact half-ml stock without using total list volume");
            check(!sent.Calls.Any(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.Text=="ادامه عکس‌ها")==true),"automatic decant reports never create a ten-photo continuation button");
            check(sent.Calls.Last(c=>c.Text.StartsWith("📸 نمایش عکس‌های دکانت پایان یافت")).Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:refs:close:"+streamView.ToString("N"))==true,
                "final photo-report summary also provides Close below the complete series so the operator need not scroll back to its first message");
            var streamViewRow=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==streamView);
            var streamedIds=JsonDocument.Parse(streamViewRow.Payload).RootElement.GetProperty("MessageIds").EnumerateArray().Select(v=>v.GetInt64()).ToArray();
            await Handle("close",streamView,message:streamClose.Id);
            check(streamedIds.All(sent.Deleted.Contains)&&!sent.Deleted.Contains(streamAddress.Id),"one Close removes the complete automatic photo series and summary while preserving the original address");
            await Handle("photos",streamRoot,message:streamAddress.Id);
            var stoppedClose=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:close:")==true)==true);
            var stoppedView=Guid.ParseExact(stoppedClose.Buttons!.SelectMany(r=>r).Single().CallbackData!.Split(':').Last(),"N");
            await Handle("close",stoppedView,message:stoppedClose.Id);var photoCountAfterStop=sent.Photos.Count;await DrainDecantReports();
            check(sent.Photos.Count==photoCountAfterStop,"closing a queued photo report cancels its remaining sends and cannot recreate group clutter");
            await Handle("photos",streamRoot,message:streamAddress.Id);
            var uncertainDecantJob=(await Rows("ShippingDecantReferenceSend")).First(n=>n.Status==NotificationOutboxStatus.Pending);
            var uncertainDecantPayload=System.Text.Json.Nodes.JsonNode.Parse(uncertainDecantJob.Payload)!;uncertainDecantPayload["Attempted"]=0;
            await db.NotificationOutbox.Where(n=>n.Id==uncertainDecantJob.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,uncertainDecantPayload.ToJsonString((JsonSerializerOptions?)null)));
            var beforeUncertainDecant=sent.Photos.Count;await DrainDecantReports();
            check(sent.Photos.Skip(beforeUncertainDecant).Count(p=>p.Success)==12&&!sent.Photos.Skip(beforeUncertainDecant).Any(p=>p.File=="stream-decant-0")&&
                sent.Calls.Last(c=>c.Text.StartsWith("📸 نمایش عکس‌های دکانت پایان یافت")).Text.Contains("تحویل تأییدنشده یا ناموفق: 1"),
                "recovery skips an interrupted unknown photo send without duplicating it and continues the remaining report with an explicit summary");
            await Handle("photos",streamRoot,message:streamAddress.Id);var beforeChangedParcel=sent.Photos.Count;
            await db.OrderItems.Where(x=>x.Id==streamItems[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RequestedVolumeMl,4m));
            await DrainDecantReports();
            check(sent.Photos.Count==beforeChangedParcel&&sent.Calls.Any(c=>c.Text.Contains("اقلام آمادهٔ بسته تغییر کرده")),
                "queued report revalidates recipient quantities and refuses stale captions after a parcel item changes");
            await db.OrderItems.Where(x=>x.Id==streamItems[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RequestedVolumeMl,3m));
            for(var n=0;n<39;n++)await Item(customer,request:streamParcel);
            var menuCallsBefore=sent.Calls.Count;await Handle("lists",streamRoot,message:streamAddress.Id);
            var allListMenus=sent.Calls.Skip(menuCallsBefore).Where(c=>c.Method=="SendInlineKeyboardAsync"&&c.Text.StartsWith("📋")).ToArray();
            var allListOptions=allListMenus.SelectMany(m=>m.Buttons!).SelectMany(r=>r).ToArray();
            check(allListMenus.Length==2&&allListOptions.Length==52&&allListMenus.All(m=>m.Buttons!.Count<=50)&&
                allListOptions.All(b=>b.Text.Contains(" | ")&&b.Text!="لیست‌های بعدی"&&System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!)<=64),
                "all 52 completed list choices appear automatically beyond fifteen, with bilingual labels and bounded Telegram keyboards rather than a Next button");
            var menuBindingsValid=true;
            foreach(var allMenu in allListMenus)
            foreach(var option in allMenu.Buttons!.SelectMany(r=>r))
            {
                var optionId=Guid.ParseExact(option.CallbackData!.Split(':').Last(),"N");
                var optionContext=JsonDocument.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==optionId)).Payload).RootElement;
                menuBindingsValid&=optionContext.GetProperty("MessageId").GetInt64()==allMenu.Id;
            }
            check(menuBindingsValid,"each automatically displayed completed-list option remains bound to its own actual menu message");
            var lastListOption=allListMenus.Last().Buttons!.SelectMany(r=>r).Last();
            await Handle("list",Guid.ParseExact(lastListOption.CallbackData!.Split(':').Last(),"N"),message:allListMenus.Last().Id);
            check(sent.Calls.Last(c=>c.File=="product-photo").Text.StartsWith("✅ لیست فروش تکمیل شد"),"a list on the final automatic menu opens normally without another menu-page click");
            var allMenuClose=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:refs:close:")==true)==true);
            var allMenuView=Guid.ParseExact(allMenuClose.Buttons!.SelectMany(r=>r).Single().CallbackData!.Split(':').Last(),"N");
            await Handle("close",allMenuView,message:allMenuClose.Id);
            check(allListMenus.All(m=>sent.Deleted.Contains(m.Id))&&!sent.Deleted.Contains(streamAddress.Id),"Close cleans every automatic completed-list menu while preserving the original package address");
            // Old messages upgrade only from an authentic callback, preserving the actual markup.
            var legacyMessage=Message(id:901) with{ReplyMarkup=new([[new("گزارش این آیدی","shipping:idreport:"+newParcel.ToString("N")),new("لینک",Url:"https://example.invalid/")]])};
            await Call("UpgradeShippingAddressReferencesAsync",new TelegramCallbackQuery("fixture",new(Operator,null),legacyMessage,"shipping:idreport:"+newParcel.ToString("N")),CancellationToken.None);
            var edit=sent.Calls.Last(c=>c.Method=="EditReplyMarkupAsync");check(edit.Buttons!.Count==5&&edit.Buttons.First().Last().Url=="https://example.invalid/"&&!edit.Buttons.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:idreport:")==true),"existing address upgrade removes retired ID report, preserves unrelated buttons and appends four references");

            // A repost contains an exact frozen snapshot but an old customer-only
            // tracking button. It must complete that parcel, never all ready items.
            var repostParcel=Guid.NewGuid();var sourceAddress=Guid.NewGuid();
            var repostFirst=await Item(customer,request:repostParcel);var repostSecond=await Item(customer,request:repostParcel);
            var newerUnassigned=await Item(customer);var unrelatedParcel=Guid.NewGuid();var unrelated=await Item(customer,request:unrelatedParcel);
            var repostIds=new[]{repostFirst.Id,repostSecond.Id};
            await db.OrderItems.Where(i=>repostIds.Contains(i.Id)).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippingTelegramMessageId,(long?)900001));
            async Task<Guid> RepostSource(long message,Guid? request,Guid[] ids,Guid? addressOverride=null,DateTime? at=null)
            {
                var id=Guid.NewGuid();db.NotificationOutbox.Add(new(){Id=id,CustomerId=customer.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=Ship.ToString(),
                    Payload=JsonSerializer.Serialize(new{CustomerId=customer.Id,RequestId=request,ItemIds=ids,ChatId=Ship.ToString(),Kind="root",MessageId=message,Installed=true,AddressId=addressOverride??sourceAddress}),
                    Status=NotificationOutboxStatus.Processed,CreatedAt=at??DateTime.UtcNow});await db.SaveChangesAsync();db.ChangeTracker.Clear();return id;
            }
            await RepostSource(900001,repostParcel,repostIds);
            var duplicateSource=await RepostSource(900002,null,repostIds);
            check(await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900002)==repostParcel,
                "reposted address resolves its frozen item snapshot to the original proven parcel instead of returning zero items");
            check(!db.ChangeTracker.HasChanges(),"tracking parcel resolution is read-only and never assigns unrelated ready items");
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900002,unrelatedParcel);}),
                "a changed/stale draft cannot override the parcel bound to the source address");
            await RepostSource(900003,null,[]);
            check(await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900003)==null,
                "empty legacy address never acquires even a single currently open parcel by customer identity");
            await RepostSource(900004,null,[repostFirst.Id,unrelated.Id]);
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900004);}),
                "mixed parcel snapshots are rejected before sending tracking");
            await RepostSource(900005,null,repostIds,Guid.NewGuid());
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900005);}),
                "reposted items cannot complete a different delivery address");
            await RepostSource(900006,null,[repostFirst.Id,outside.Id]);
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900006);}),
                "snapshot containing another customer's item cannot authorize dispatch");
            await RepostSource(900007,null,[newerUnassigned.Id]);
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900007);}),
                "unassigned snapshot cannot silently send tracking and report zero without parcel review");
            await RepostSource(900008,null,repostIds,at:DateTime.UtcNow.AddDays(-1));
            check(await Throws(async()=>{await ShippingTrackingParcelResolver.ResolveAsync(db,customer.Id,Ship.ToString(),900008);}),
                "old address snapshot cannot ship items assigned to a later parcel");
            await Call("TryHandleShippingRequestCallbackAsync",new TelegramCallbackQuery("fixture",new(Operator,null),Message(id:900002),"shipping:trackingcustomer:"+customer.Id.ToString("N")),CancellationToken.None);
            check(trackingStore.TryGetShippingTrackingPhoto(Ship,Operator,out var repostDraft)&&repostDraft.ShippingRequestId==repostParcel,
                "customer-only callback records the original parcel resolved from this exact repost");
            // Also cover an in-flight draft captured before the upgrade.
            trackingStore.SetShippingTrackingPhoto(new(){ChatId=Ship,UserId=Operator,CustomerId=customer.Id,ShippingRequestId=Guid.Empty,SourceMessageId=900002});
            var repostPhotosBefore=sent.Photos.Count;
            await Call("TryHandleShippingTrackingPhotoMessageAsync",new TelegramMessage(null,new(Ship,"supergroup"),new(Operator,null),null,Photo:[new("repost-tracking-photo",100,100)],MessageId:900010),CancellationToken.None);
            check(sent.Photos.Count==repostPhotosBefore+1&&sent.Photos.Last().Chat==Client.ToString(),
                "in-flight customer-only photo reaches the proper customer once after exact source recovery");
            check(await db.OrderItems.CountAsync(i=>repostIds.Contains(i.Id)&&i.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped)==2,
                "tracking photo for reposted address completes its original two-item package");
            check(await db.OrderItems.CountAsync(i=>(i.Id==newerUnassigned.Id||i.Id==unrelated.Id)&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null)==2,
                "reposted tracking leaves newer unassigned and other-parcel ready items untouched");
            check((await Rows(ShippingTrackingNotices.Channel)).Any(n=>JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(n.Payload) is var notice&&notice!.SourceMessageId==900002&&notice.RequestId==repostParcel),
                "tracking success notice durably records the recovered original parcel under the reposted address");
        }
        finally
        {
            db.ChangeTracker.Clear();var customers=new[]{customer.Id,other.Id};
            await db.NotificationOutbox.Where(n=>customers.Contains(n.CustomerId)).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>lists.Contains(r.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>lists.Contains(l.Id)).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>customers.Contains(g.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>customers.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
    static async Task<bool> Throws(Func<Task> action){try{await action();return false;}catch(InvalidOperationException){return true;}}
    static async Task Transport(Action<bool,string> check)
    {
        using var sender=new TelegramMessageSender(Options.Create(new TelegramOptions{BotToken="fixture"}));var field=typeof(TelegramMessageSender).GetField("_httpClient",Private)!;((HttpClient)field.GetValue(sender)!).Dispose();
        var handler=new ShippingPhotoHttpHandler();field.SetValue(sender,new HttpClient(handler){BaseAddress=new Uri("https://example.invalid/")});
        var failed=await sender.SendPhotoOnceWithKeyboardAsync("-1","photo","caption",[]);check(!failed.IsSuccessful&&!failed.IsRetryable&&handler.Calls==1,"ambiguous photo HTTP failure is attempted once with no unsafe automatic retry");
        handler.Status=HttpStatusCode.TooManyRequests;var throttled=await sender.SendPhotoOnceWithKeyboardAsync("-1","photo","caption",[]);
        check(throttled.IsRetryable&&throttled.RetryAfterSeconds==3&&handler.Calls==2,"single-attempt photo transport exposes only definite Telegram 429 cooldown as retryable");
        handler.Status=HttpStatusCode.OK;var reset=await sender.SendKeyboardDismissAsync("-1","فرم بسته شد");
        using var resetBody=JsonDocument.Parse(handler.Payload!);var markup=resetBody.RootElement.GetProperty("reply_markup");
        check(reset.IsSuccessful&&markup.GetProperty("remove_keyboard").GetBoolean()&&!markup.GetProperty("selective").GetBoolean()&&!markup.TryGetProperty("force_reply",out _),"reply-dismiss transport sends real Telegram keyboard removal and never recreates ForceReply");
        var visibleAck=await sender.SendReplyOnceAsync("-1","تایید شد ✅️",777);
        using var ackBody=JsonDocument.Parse(handler.Payload!);
        check(visibleAck.IsSuccessful&&ackBody.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64()==777,
            "visible customer confirmation uses Telegram's real reply parameters to stay under the correct photo");
        var append=await sender.SendPhotoReplyOnceWithKeyboardAsync("-1","next-photo","عکس دوم",888,[]);
        using var appendBody=JsonDocument.Parse(handler.Payload!);
        check(append.IsSuccessful&&appendBody.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64()==888,
            "additional package photo transport replies to the actual first customer photo");
    }
}
public sealed record ShippingReferenceCall(string Method,string Chat,string Text,long Id,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>? Buttons=null,string? File=null,bool Success=true,long? ReplyTo=null);
public class ShippingReferenceSender:DispatchProxy
{
    public string? AccountingChat;public HashSet<string> AccountingAdmins=[];
    public long NextId=600;public bool Admin=true;public Func<TelegramSendResult>? PhotoResult;public Func<string,string,TelegramSendResult>? TextResult;public List<long> Deleted=[];public long? FailDeleteId;
    public List<ShippingReferenceCall> Calls=[];public List<ShippingReferenceCall> Photos=>Calls.Where(c=>c.File is not null).ToList();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var name=method!.Name;if(name=="IsChatAdministratorAsync")return Task.FromResult(args![0]?.ToString()==AccountingChat?AccountingAdmins.Contains(args[1]?.ToString()??""):Admin);
        if(name=="DeleteMessageAsync"){var target=(long)args![1]!;if(target==FailDeleteId)return Task.FromResult(new TelegramSendResult(false,"temporary delete failure"));Deleted.Add(target);return Task.FromResult(new TelegramSendResult(true));}
        var chat=args![0]?.ToString()??"";var id=++NextId;var text=args.Length>1?args[1]?.ToString()??"":"";string? file=null;
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>? buttons=null;var result=new TelegramSendResult(true,MessageId:id);
        if(name=="EditReplyMarkupAsync"){text="";buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[2]!;result=result with{MessageId=(long)args[1]!};}
        if(name=="EditTextWithKeyboardAsync"){text=(string)args[2]!;buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!;result=result with{MessageId=(long)args[1]!};}
        if(name=="SendInlineKeyboardAsync")buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[2]!;
        if(name.Contains("Photo")&&!name.Contains("Prompt")){file=(string)args[1]!;text=(string)args[2]!;if(name=="SendPhotoOnceWithKeyboardAsync"){buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!;result=PhotoResult?.Invoke()??result;}}
        if(name=="SendPhotoReplyOnceWithKeyboardAsync"){buttons=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[4]!;result=PhotoResult?.Invoke()??result;}
        if(name is "SendReplyOnceAsync" or "SendKeyboardDismissAsync")result=TextResult?.Invoke(chat,text)??result;
        Calls.Add(new(name,chat,text,result.MessageId??id,buttons,file,result.IsSuccessful,name=="SendReplyOnceAsync"?(long)args[2]!:name=="SendPhotoReplyOnceWithKeyboardAsync"?(long)args[3]!:null));return Task.FromResult(result);
    }
}
public class ShippingPhotoHttpHandler:HttpMessageHandler
{
    public int Calls;public string? Payload;public HttpStatusCode Status=HttpStatusCode.BadGateway;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {Calls++;Payload=await request.Content!.ReadAsStringAsync(ct);return new HttpResponseMessage(Status){Content=new StringContent(Status==HttpStatusCode.TooManyRequests?"{\"ok\":false,\"parameters\":{\"retry_after\":3}}":Status==HttpStatusCode.OK?"{\"ok\":true,\"result\":{\"message_id\":1}}":"{\"ok\":false}")};}
}
