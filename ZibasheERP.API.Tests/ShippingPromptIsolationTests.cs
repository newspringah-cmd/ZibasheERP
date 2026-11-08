using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class ShippingPromptIsolationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=771234567,chat=-1004432663999;
        var customer=new Customer{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,FullName="Shipping prompt fixture",Mobile=Guid.NewGuid().ToString("N")[..18],TelegramId=owner.ToString()};
        var group=new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chat.ToString(),Title="Shipping fixture",CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow};
        db.Customers.Add(customer);db.CustomerTelegramGroups.Add(group);await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingPromptSender>();var sent=(ShippingPromptSender)(object)sender;
        var drafts=new TelegramOrderFlowDraftStore();
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString()}),("_orderFlowDrafts",drafts),("_adminActivityContext",new AdminActivityContext()),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        var draft=new TelegramShippingPreparationDraft{ChatId=chat,UserId=owner,CustomerId=customer.Id,Stage=TelegramShippingPreparationStage.AwaitingNewAddress};drafts.SetShippingPreparation(draft);
        TelegramMessage Message(string text,long? reply=null,bool bot=true)=>new(text,new(chat,"supergroup","Shipping fixture"),new(owner,null),null,MessageId:400,ReplyToMessage:reply.HasValue?new("آدرس کامل را وارد کنید",new(chat,"supergroup"),new(123,null,IsBot:bot),null,MessageId:reply.Value):null);
        Task<bool> Handle(TelegramMessage message)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleShippingPreparationMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[message,CancellationToken.None])!;
        async Task Prompt()=>await (Task)typeof(TelegramWebhookController).GetMethod("SendShippingInputPromptAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[chat,"آدرس کامل را وارد کنید",CancellationToken.None,owner])!;
        try
        {
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
            check(await Handle(Message("تهران خیابان بهار پلاک ۱۰",correct)),"authorized reply to the exact current address prompt still registers the real address");
            check(await db.Addresses.CountAsync(a=>a.CustomerId==customer.Id)==1&&(await db.Addresses.SingleAsync(a=>a.CustomerId==customer.Id)).FullAddress=="تهران خیابان بهار پلاک ۱۰","only the real submitted address is stored; customer questions remain absent");
            check(!await Handle(Message("زیبا وضعیت عطرها",346)),"ready-to-confirm address stage does not intercept unrelated status questions");
            draft.Stage=TelegramShippingPreparationStage.AwaitingNewAddress;drafts.SetShippingPreparation(draft);sent.FailPrompt=true;await Prompt();
            check(draft.InputPromptMessageId is null&&!await Handle(Message("آدرس جدید",correct)),"failed replacement prompt invalidates the previous prompt instead of accepting stale input");
        }
        finally
        {
            await db.Addresses.Where(a=>a.CustomerId==customer.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>g.Id==group.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id).ExecuteDeleteAsync();
        }
    }
}
public class ShippingPromptSender:DispatchProxy
{
    public long LastId=1000;public bool FailPrompt;public List<string> Replies=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name is "SendForceReplyAsync" or "SendKeyboardDismissAsync")return Task.FromResult(FailPrompt?new TelegramSendResult(false):new TelegramSendResult(true,MessageId:++LastId));
        if(method.Name=="SendReplyAsync"){Replies.Add((string)args![1]!);return Task.FromResult(new TelegramSendResult(true,MessageId:++LastId));}
        if(method.Name is "SendAsync" or "SendInlineKeyboardAsync" or "DeleteMessageAsync")return Task.FromResult(new TelegramSendResult(true,MessageId:++LastId));
        if(method.Name=="IsChatAdministratorAsync")return Task.FromResult(true);
        throw new NotSupportedException(method.Name);
    }
}
