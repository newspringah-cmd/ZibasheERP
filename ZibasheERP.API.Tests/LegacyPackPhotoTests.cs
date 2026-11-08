using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class LegacyPackPhotoTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new Exception("Development database only");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long op=741471928,ship=-10098112233,client=-10098112234,bot=881122,user=88119943;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var marker="legacy_pack_"+Guid.NewGuid().ToString("N")[..12];
        var customer=new Customer{Id=Guid.NewGuid(),FullName=marker,Username=marker,Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var address=new Address{Id=Guid.NewGuid(),CustomerId=customer.Id,ReceiverName=marker,FullAddress="Synthetic address",Mobile="09100000000",CreatedAt=DateTime.UtcNow};
        var group=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=client.ToString(),Title=marker,IsActive=true,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        var root=Guid.NewGuid();
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingReferenceSender>();var sent=(ShippingReferenceSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=op.ToString(),ShippingChatId=ship.ToString(),BotToken=bot+":fixture"}),
            ("_orderFlowDrafts",new TelegramOrderFlowDraftStore()),("_decantPhotoDrafts",new TelegramDecantPhotoDraftStore()),("_logger",NullLogger<TelegramWebhookController>.Instance)})
            typeof(TelegramWebhookController).GetField(name,flags)!.SetValue(controller,value);
        async Task<object?> Call(string name,params object?[] args){var task=(Task)typeof(TelegramWebhookController).GetMethod(name,flags)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        TelegramMessage Message(long chat,long id)=>new(null,new(chat,"supergroup"),new(bot,null,IsBot:true),null,MessageId:id);
        async Task Handle(string action,Guid id,long message,long actor=op,long chat=ship,string? username=null)=>await Call("TryHandleShippingReferenceCallbackAsync",new TelegramCallbackQuery("fixture",new(actor,username),Message(chat,message),"shipping:refs:"+action+":"+id.ToString("N")),CancellationToken.None);
        async Task<NotificationOutbox[]> Rows(string channel)=>await db.NotificationOutbox.AsNoTracking().Where(n=>n.CustomerId==customer.Id&&n.Channel==channel).OrderByDescending(n=>n.CreatedAt).ToArrayAsync();
        async Task<ShippingPackApprovalData> Approval(Guid id)=>JsonSerializer.Deserialize<ShippingPackApprovalData>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id)).Payload)!;
        async Task Upload(string file,long id)=>await Call("TryHandleShippingPackPhotoMessageAsync",new TelegramMessage(null,new(ship,"supergroup"),new(op,null),null,Photo:[new(file,100,100)],MessageId:id,Date:DateTimeOffset.UtcNow.ToUnixTimeSeconds()),CancellationToken.None);
        try
        {
            db.AddRange(customer,address,group);db.NotificationOutbox.Add(new(){Id=root,CustomerId=customer.Id,Channel="ShippingReferenceContext",EventType="AddressReferences",Recipient=ship.ToString(),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,
                Payload=JsonSerializer.Serialize(new{CustomerId=customer.Id,RequestId=(Guid?)null,ItemIds=Array.Empty<Guid>(),ChatId=ship.ToString(),Kind="root",MessageId=1000L,Installed=true,AddressId=address.Id})});await db.SaveChangesAsync();
            sent.Admin=false;await Handle("start",root,1000,999);sent.Admin=true;
            check((await Rows("ShippingPackPhotoDraft")).Length==0,"legacy photo form still requires authorized shipping staff");
            await Handle("start",root,999);
            check((await Rows("ShippingPackPhotoDraft")).Length==0,"a legacy address button cannot be replayed from a different message");
            await Handle("start",root,1000);
            var draft=(await Rows("ShippingPackPhotoDraft")).Single();var data=JsonNode.Parse(draft.Payload)!;
            check(data["Reference"]!["LegacyAddressOnly"]!.GetValue<bool>()&&sent.Calls.Last(c=>c.Text.StartsWith("📷 بستهٔ")).Text.Contains("اقلام ثبت‌شدهٔ قبلی"),"a customer with no orders or invoices can open photo collection from their own stored address");
            await Upload("legacy-photo-1",2001);var question=sent.Calls.Last(c=>c.Text.Contains("آیا عکس دیگری دارید؟"));
            check(sent.Photos.Count==0&&question.Buttons!.SelectMany(r=>r).Any(b=>b.Text=="بله"),"legacy photo is collected and shows Yes/No without prematurely delivering to customer");
            await Handle("add",draft.Id,question.Id);await Upload("legacy-photo-2",2002);question=sent.Calls.Last(c=>c.Text.Contains("آیا عکس دیگری دارید؟"));
            await Handle("send",draft.Id,question.Id);await Handle("send",draft.Id,question.Id);
            var receipt=(await Rows(ShippingPackApproval.ReceiptChannel)).Single();var approval=await Approval(receipt.Id);
            check(approval.LegacyAddressOnly&&approval.ItemIds.Length==0&&approval.RequestId is null&&approval.AddressId==address.Id&&approval.AddressMessageId==1000,
                "legacy approval is bound to the exact customer address without inventing ERP items or a shipping request");
            check((await Rows(ShippingPackApproval.SendChannel)).Length==1&&receipt.Recipient.StartsWith("legacy-address:"),"repeated No clicks create only one durable legacy photo delivery scoped to this address");
            var job=(await Rows(ShippingPackApproval.SendChannel)).Single();
            sent.PhotoResult=()=>new(false,"cooldown"){IsRetryable=true,RetryAfterSeconds=17};
            check((int?)await Call("ProcessShippingPackApprovalSendAsync",job,CancellationToken.None)==17,"legacy photo delivery preserves exact Telegram retry_after");
            sent.PhotoResult=null;await Call("ProcessShippingPackApprovalSendAsync",job,CancellationToken.None);
            approval=await Approval(receipt.Id);var photos=sent.Photos.Where(p=>p.Success&&p.Chat==client.ToString()).ToArray();
            check(approval.SentAt.HasValue&&photos.Length==2&&photos.Last().Text==ShippingPackApproval.Caption,"both legacy photos reach only the linked accounting group with approval controls");
            check(!await db.Orders.AnyAsync(o=>o.CustomerId==customer.Id)&&!await db.Invoices.AnyAsync(i=>i.Order!.CustomerId==customer.Id),"photo-only delivery does not create financial orders or invoices");
            await Handle("approve",receipt.Id,photos.Last().Id,user,client,"wrong_username");
            check(!(await Approval(receipt.Id)).ConfirmedAt.HasValue,"a different customer cannot approve legacy photos by sharing the chat");
            await Handle("approve",receipt.Id,photos.Last().Id,user,client,customer.Username);
            check((await Approval(receipt.Id)).State==ShippingPackApprovalState.Confirmed,"the correctly identified legacy customer can approve without any ERP invoice");
            var response=(await Rows("ShippingPackCustomerResponse")).Single();await Call("ProcessShippingPackResponseAsync",response,CancellationToken.None);
            check(sent.Calls.Any(c=>c.Chat==client.ToString()&&c.Text=="تایید شد ✅️")&&sent.Calls.Any(c=>c.Chat==ship.ToString()&&c.Text.StartsWith("✅ تأیید مشتری")&&c.ReplyTo==1000),
                "legacy confirmation appears below customer photo and is threaded to the exact preparation address");
            await ShippingPackApproval.MarkDispatchedAsync(db,customer.Id,null,[]);
            check((await Approval(receipt.Id)).State==ShippingPackApprovalState.Confirmed,"empty ERP dispatch cannot mark a legacy photo-only parcel as shipped");
            var cleanup=sent.Calls.Last(c=>c.Buttons?.SelectMany(r=>r).Any(b=>b.CallbackData=="shipping:refs:packclear:"+draft.Id.ToString("N"))==true);
            await Handle("packclear",draft.Id,cleanup.Id);
            check(sent.Deleted.Contains(2001)&&sent.Deleted.Contains(2002)&&!sent.Deleted.Contains(1000)&&!photos.Any(p=>sent.Deleted.Contains(p.Id)),"legacy cleanup removes operator extras while preserving customer photos and original address");
            await Handle("start",root,1000);draft=(await Rows("ShippingPackPhotoDraft")).First();await Upload("legacy-revision-photo",2003);question=sent.Calls.Last(c=>c.Text.Contains("آیا عکس دیگری دارید؟"));await Handle("send",draft.Id,question.Id);
            receipt=(await Rows(ShippingPackApproval.ReceiptChannel)).First();job=(await Rows(ShippingPackApproval.SendChannel)).First();await Call("ProcessShippingPackApprovalSendAsync",job,CancellationToken.None);
            approval=await Approval(receipt.Id);var delivered=approval.Deliveries.Single();
            await Handle("revise",receipt.Id,delivered.MessageId!.Value,user,client,customer.Username);
            var revision=(await Rows("ShippingPackRevisionDraft")).Single();var revisionPrompt=JsonNode.Parse(revision.Payload)!["PromptId"]!.GetValue<long>();
            await Call("TryHandleShippingPackRevisionMessageAsync",new TelegramMessage("شیشهٔ دوم را بررسی کنید",new(client,"supergroup"),new(user,customer.Username),null,MessageId:3001,ReplyToMessage:Message(client,revisionPrompt)),CancellationToken.None);
            var preview=sent.Calls.Last(c=>c.Text.StartsWith("✏️ توضیحات اصلاح بسته:"));await Handle("fixsend",revision.Id,preview.Id,user,client,customer.Username);
            var review=(await Rows("ShippingPackCustomerResponse")).First(n=>n.EventType=="revision");await Call("ProcessShippingPackResponseAsync",review,CancellationToken.None);
            check(sent.Calls.Any(c=>c.Chat==client.ToString()&&c.Text=="برای بررسی مجدد ارسال شد")&&sent.Calls.Any(c=>c.Chat==ship.ToString()&&c.Text.Contains("شیشهٔ دوم را بررسی کنید")&&c.ReplyTo==1000),
                "legacy customer correction text reaches preparation and receives a review-sent acknowledgement");
            var before=sent.Photos.Count;var forged=approval with{AddressId=Guid.NewGuid(),State=ShippingPackApprovalState.Sending,Deliveries=[],SentAt=null,OperatorNoticeAttemptedAt=null};
            await db.NotificationOutbox.Where(n=>n.Id==receipt.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,JsonSerializer.Serialize(forged,(JsonSerializerOptions?)null)));
            await Call("ProcessShippingPackApprovalSendAsync",job,CancellationToken.None);
            check(sent.Photos.Count==before&&(await Approval(receipt.Id)).State==ShippingPackApprovalState.NeedsReview,"worker independently refuses a forged or deleted legacy address without sending photos");
        }
        finally
        {
            await db.NotificationOutbox.Where(n=>n.CustomerId==customer.Id).ExecuteDeleteAsync();await db.Addresses.Where(a=>a.CustomerId==customer.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>g.CustomerId==customer.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id).ExecuteDeleteAsync();
        }
    }
}
