using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class ShippingPromptIsolationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=771234567,chat=-1004432663999,preparation=-1004432664000;
        var customer=new Customer{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,FullName="Shipping prompt fixture",Mobile=Guid.NewGuid().ToString("N")[..18],TelegramId=owner.ToString()};
        var group=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chat.ToString(),Title="Shipping fixture",CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        db.Customers.Add(customer);db.CustomerTelegramGroups.Add(group);await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingPromptSender>();var sent=(ShippingPromptSender)(object)sender;
        var drafts=new TelegramOrderFlowDraftStore();
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),ShippingChatId=preparation.ToString()}),("_orderFlowDrafts",drafts),("_adminActivityContext",new AdminActivityContext()),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        var draft=new TelegramShippingPreparationDraft{ChatId=chat,UserId=owner,CustomerId=customer.Id,Stage=TelegramShippingPreparationStage.AwaitingNewAddress};drafts.SetShippingPreparation(draft);
        TelegramMessage Message(string text,long? reply=null,bool bot=true)=>new(text,new(chat,"supergroup","Shipping fixture"),new(owner,null),null,MessageId:400,ReplyToMessage:reply.HasValue?new("آدرس کامل را وارد کنید",new(chat,"supergroup"),new(123,null,IsBot:bot),null,MessageId:reply.Value):null);
        Task<bool> Handle(TelegramMessage message)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleShippingPreparationMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[message,CancellationToken.None])!;
        async Task Prompt()=>await (Task)typeof(TelegramWebhookController).GetMethod("SendShippingInputPromptAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[chat,"آدرس کامل را وارد کنید",CancellationToken.None,owner])!;
        try
        {
            bool Guidance(string text)=>(bool)typeof(TelegramWebhookController).GetMethod("IsPerfumeGuidanceQuestion",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[text])!;
            const string postalAddress="گیرنده تست\n09120000000\nتهران_جنت آباد مرکزی _ ۲۰ متری گلستان شرقی _ خیابان شهید معینی _ کوچه دوازدهم _ پلاک ۲۳ _ واحد۳ ( در صورت نبودن به واحد یک تحویل داده شود )";
            check(!Guidance(postalAddress),"postal address does not mistake نت inside جنت or بو inside نبودن for perfume subjects");
            check(Guidance("عطر خنک پیشنهاد میدی؟")&&Guidance("نت‌های چوبی و پخش بو"),"whole perfume terms and compound note questions still reach fragrance guidance");
            await Prompt();check(draft.InputPromptMessageId==sent.LastId,"shipping draft records the exact successfully sent non-forcing group prompt ID");
            var correct=draft.InputPromptMessageId!.Value;
            check(!await Handle(Message("زیبا وضعیت عطرا",346))&&!await db.Addresses.AnyAsync(a=>a.CustomerId==customer.Id),"status question replying to an invoice cannot create an address");
            var question=Message("زیبا وضعیت عطرها",346);
            var answered=await (Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleCustomerStatusQuestionAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[question,CancellationToken.None])!;
            check(answered&&sent.Replies.Count==1&&sent.Replies[0].Contains("آیتم سفارشی"),"status question still reaches its independent status-answer route while address form is pending");
            check(!await Handle(Message("زیبا وضعیت عطرها",correct)),"explicit customer status question is never stored as address even when replying to the address prompt");
            check(!await Handle(Message("تهران خیابان بهار پلاک ۱۰",correct-1)),"old prompt with identical text cannot capture a new address");
            check(!await Handle(Message("تهران خیابان بهار پلاک ۱۰",correct,false)),"human message with matching ID and prompt-like text cannot impersonate the bot address prompt");
            check(!await Handle(Message("تهران خیابان بهار پلاک ۱۰")),"ordinary group messages remain outside the address form");
            check(await Handle(Message(postalAddress,correct)),"authorized postal address with ambiguous fragrance substrings is accepted on first submission");
            check(await db.Addresses.CountAsync(a=>a.CustomerId==customer.Id)==1&&(await db.Addresses.SingleAsync(a=>a.CustomerId==customer.Id)).FullAddress==postalAddress,"only the real submitted address is stored verbatim; customer questions remain absent");
            check(!await Handle(Message("زیبا وضعیت عطرها",346)),"ready-to-confirm address stage does not intercept unrelated status questions");
            draft.Stage=TelegramShippingPreparationStage.AwaitingNewAddress;drafts.SetShippingPreparation(draft);sent.FailPrompt=true;await Prompt();
            check(draft.InputPromptMessageId is null&&!await Handle(Message("آدرس جدید",correct)),"failed replacement prompt invalidates the previous prompt instead of accepting stale input");
            drafts.ClearShippingPreparation(chat,owner);
            check(await Handle(Message(postalAddress,correct)),"after draft loss an authorized direct bot-prompt reply recovers exact binding and completes address registration");
            check(drafts.TryGetShippingPreparation(chat,owner,out var recovered)&&recovered.Stage==TelegramShippingPreparationStage.Ready&&recovered.InputPromptMessageId==correct,"recovered address draft retains exact prompt ID and advances to confirmation");
            await VerifyUnpaidAddressAsync(db, controller, sent, drafts, customer, Message("/ad"), check);
            await VerifyInPersonPickupAsync(db, controller, sent, drafts, customer, Message("/ad"), check);
        }
        finally
        {
            await db.Addresses.Where(a=>a.CustomerId==customer.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>g.Id==group.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id).ExecuteDeleteAsync();
        }
    }

    private static async Task VerifyUnpaidAddressAsync(AppDbContext db, TelegramWebhookController controller,
        ShippingPromptSender sender, TelegramOrderFlowDraftStore drafts, Customer customer, TelegramMessage source, Action<bool, string> check)
    {
        const BindingFlags privateMethods = BindingFlags.Instance | BindingFlags.NonPublic;
        var chatId = source.Chat.Id;
        var operatorId = source.From!.Id;
        var order = new Order { Id = Guid.NewGuid(), CustomerId = customer.Id, OrderNumber = Guid.NewGuid().ToString("N"), Status = OrderStatus.Invoiced };
        var laterOrder = new Order { Id = Guid.NewGuid(), CustomerId = customer.Id, OrderNumber = Guid.NewGuid().ToString("N"), Status = OrderStatus.Invoiced };
        Invoice Outstanding(Order invoiceOrder) => new() { Id = Guid.NewGuid(), OrderId = invoiceOrder.Id, InvoiceNumber = Guid.NewGuid().ToString("N"),
            Status = InvoiceStatus.Issued, TotalAmount = 100000 };
        var first = Outstanding(order);
        var second = Outstanding(laterOrder);
        db.AddRange(order, first);
        await db.SaveChangesAsync();
        try
        {
            const string text = "تهران خیابان نمونه پلاک ۲ برای آزمون بدهی";
            var draft = new TelegramShippingPreparationDraft
            {
                ChatId = chatId, UserId = operatorId, CustomerId = customer.Id,
                Stage = TelegramShippingPreparationStage.AwaitingNewAddress, InputPromptMessageId = 400
            };
            drafts.SetShippingPreparation(draft);
            var input = source with { Text = text, ReplyToMessage = source with
            {
                Text = "آدرس کامل را وارد کنید", From = new TelegramUser(123, null, IsBot: true), MessageId = 400
            }};
            Task<bool> Handle(TelegramMessage message) => (Task<bool>)typeof(TelegramWebhookController)
                .GetMethod("TryHandleShippingPreparationMessageAsync", privateMethods)!.Invoke(controller, [message, CancellationToken.None])!;
            Task Confirm(Guid token, TelegramUser actor, long chat) => (Task)typeof(TelegramWebhookController)
                .GetMethod("ConfirmShippingPaymentOverrideAsync", privateMethods)!.Invoke(controller,
                    [new TelegramCallbackQuery("approval", actor, source with { Chat = source.Chat with { Id = chat } }, "approval"), token, CancellationToken.None])!;
            Task<bool> Saved() => db.Addresses.AnyAsync(a => a.CustomerId == customer.Id && a.FullAddress == text);
            check(await Handle(input) && draft.PaymentApproval is not null && !await Saved(),
                "an issued invoice blocks the real raw-address path before anything is saved");
            var token = draft.PaymentApproval!.Token;
            await Confirm(token, source.From with { IsBot = true }, chatId);
            await Confirm(token, source.From with { Id = operatorId + 1 }, chatId);
            await Confirm(token, source.From, chatId + 1);
            check(!await Saved() && draft.PaymentApproval?.Token == token, "automated actors and a different chat cannot redeem an admin approval");
            db.AddRange(laterOrder, second);
            await db.SaveChangesAsync();
            await Confirm(token, source.From, chatId);
            check(!await Saved() && draft.PaymentApproval is { } replacement && replacement.Token != token,
                "a newly issued unpaid invoice requires a fresh confirmation before saving the address");
            var fresh = draft.PaymentApproval!.Token;
            await Confirm(fresh, source.From, chatId);
            await Confirm(fresh, source.From, chatId);
            check(await db.Addresses.CountAsync(a => a.CustomerId == customer.Id && a.FullAddress == text) == 1 &&
                sender.LastCallback?.Contains("معتبر نیست") == true, "the human confirmation saves the exact original address once and replay is rejected");

            draft.ApprovedUnpaidInvoices = [];
            await (Task)typeof(TelegramWebhookController).GetMethod("DispatchPreparedShippingRequestAsync", privateMethods)!.Invoke(controller,
                [new TelegramCallbackQuery("dispatch", source.From, source, "shipping:preparedispatch"), CancellationToken.None])!;
            check(draft.PaymentApproval?.AddressId == draft.AddressId && sender.LastCallback?.Contains("متوقف شد") == true,
                "dispatch of an already saved address is blocked before posting in the preparation group");
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Invoices.Where(i => i.OrderId == order.Id || i.OrderId == laterOrder.Id).ExecuteDeleteAsync();
            await db.Orders.Where(o => o.Id == order.Id || o.Id == laterOrder.Id).ExecuteDeleteAsync();
        }
    }

    private static async Task VerifyInPersonPickupAsync(AppDbContext db, TelegramWebhookController controller,
        ShippingPromptSender sender, TelegramOrderFlowDraftStore drafts, Customer customer, TelegramMessage command,
        Action<bool, string> check)
    {
        const BindingFlags privateMethods = BindingFlags.Instance | BindingFlags.NonPublic;
        var commandHandler = typeof(TelegramWebhookController).GetMethod("TryHandleShippingPreparationCommandAsync", privateMethods)!;
        var pickupHandler = typeof(TelegramWebhookController).GetMethod("TryHandleInPersonPickupMessageAsync", privateMethods)!;
        Task<bool> Pickup(TelegramMessage message) => (Task<bool>)pickupHandler.Invoke(controller, [message, CancellationToken.None])!;
        var pickup = command with { Text = "  تحويل   حضوری ", MessageId = 401 };
        var otherCustomer = new Customer { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, FullName = "Other fixture",
            Mobile = Guid.NewGuid().ToString("N")[..18] };
        Order OrderFor(Customer profile) => new()
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CustomerId = profile.Id,
            OrderNumber = Guid.NewGuid().ToString("N")[..20], Status = OrderStatus.ReadyToShip
        };
        OrderItem ItemFor(Order order, string name, OrderItemFulfillmentStatus status = OrderItemFulfillmentStatus.DecantedReadyToShip) => new()
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, OrderId = order.Id,
            ManualDescription = name, FulfillmentStatus = status, RequestedVolumeMl = 2
        };
        var order = OrderFor(customer);
        var otherOrder = OrderFor(otherCustomer);
        var ready = ItemFor(order, "Pickup perfume");
        var reserved = ItemFor(order, "Already in postal parcel");
        var openParcel = Guid.NewGuid();
        reserved.ShippingRequestId = openParcel;
        var notReady = ItemFor(order, "Still being decanted", OrderItemFulfillmentStatus.DecantQueue);
        var unrelated = ItemFor(otherOrder, "Other customer's perfume");
        var orderIds = new[] {order.Id, otherOrder.Id};
        db.Customers.Add(otherCustomer);
        db.Orders.AddRange(order, otherOrder);
        db.OrderItems.AddRange(ready, reserved, notReady, unrelated);
        await db.SaveChangesAsync();
        try
        {
            check(await (Task<bool>)commandHandler.Invoke(controller, [command, CancellationToken.None])! &&
                sender.LastKeyboard?.Contains("«تحویل حضوری» را تایپ کنید") == true,
                "/ad offers a typed in-person pickup in the linked customer group");
            check(!await Pickup(pickup with { From = new TelegramUser(999, null) }),
                "a customer message cannot trigger the operator's pickup draft");
            sender.FailText = true;
            check(await Pickup(pickup) && (await db.OrderItems.AsNoTracking().SingleAsync(i => i.Id == ready.Id))
                is { FulfillmentStatus: OrderItemFulfillmentStatus.DecantedReadyToShip, ShippingRequestId: null, ShippedAt: null },
                "a rejected preparation-group post rolls back the in-person pickup so it can be retried");
            sender.FailText = false;
            check(await Pickup(pickup) && sender.LastText is { } report &&
                report.StartsWith("❌️❌️❌️تحویل حضوری❌️❌️❌️\n", StringComparison.Ordinal) &&
                report.Contains("Pickup perfume") && !report.Contains("Already in postal parcel") &&
                !report.Contains("Other customer's perfume") && sender.LastReplyTo == pickup.MessageId &&
                sender.Replies.Last() == "جهت آماده سازی ارسال شد",
                "pickup sends the preparation report with the exact header, then replies directly to the operator");
            var stored = await db.OrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToDictionaryAsync(i => i.Id);
            check(stored[ready.Id] is { FulfillmentStatus: OrderItemFulfillmentStatus.Shipped, ShippedAt: not null,
                      ShippingRequestId: not null, ShippingTelegramMessageId: not null } &&
                stored[reserved.Id].ShippingRequestId == openParcel && stored[reserved.Id].ShippedAt is null &&
                stored[notReady.Id].FulfillmentStatus == OrderItemFulfillmentStatus.DecantQueue && stored[unrelated.Id].ShippedAt is null,
                "only ready unassigned items of the selected customer change to shipped");
            check(!drafts.TryGetShippingPreparation(command.Chat.Id, command.From!.Id, out _) && !await Pickup(pickup),
                "a repeated pickup message cannot repost a completed request");
        }
        finally
        {
            await db.OrderItems.Where(i => orderIds.Contains(i.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(o => orderIds.Contains(o.Id)).ExecuteDeleteAsync();
            await db.Customers.Where(c => c.Id == otherCustomer.Id).ExecuteDeleteAsync();
        }
    }
}
public class ShippingPromptSender:DispatchProxy
{
    public long LastId=1000;public bool FailPrompt;public List<string> Replies=[];
    public bool FailText;
    public string? LastText, LastKeyboard, LastCallback;
    public long LastReplyTo;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name is "SendForceReplyAsync" or "SendKeyboardDismissAsync")return Task.FromResult(FailPrompt?new TelegramSendResult(false):new TelegramSendResult(true,MessageId:++LastId));
        if(method.Name=="SendReplyAsync"){Replies.Add((string)args![1]!);LastReplyTo=(long)args[2]!;return Task.FromResult(new TelegramSendResult(true,MessageId:++LastId));}
        if(method.Name=="SendAsync"){LastText=(string)args![1]!;return Task.FromResult(FailText?new TelegramSendResult(false):new TelegramSendResult(true,MessageId:++LastId));}
        if(method.Name=="SendInlineKeyboardAsync")LastKeyboard=(string)args![1]!;
        if(method.Name=="AnswerCallbackAsync"){LastCallback=(string?)args![1];return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name is "SendInlineKeyboardAsync" or "DeleteMessageAsync")return Task.FromResult(new TelegramSendResult(true,MessageId:++LastId));
        if(method.Name=="IsChatAdministratorAsync")return Task.FromResult(true);
        throw new NotSupportedException(method.Name);
    }
}
