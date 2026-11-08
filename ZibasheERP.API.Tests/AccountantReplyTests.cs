using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.CustomerAssistant;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class AccountantReplyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using(var safeSender=new TelegramMessageSender(Microsoft.Extensions.Options.Options.Create(new TelegramOptions{BotToken="test-token"})))
        {
            var field=typeof(TelegramMessageSender).GetField("_httpClient",BindingFlags.Instance|BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(safeSender)!).Dispose();var handler=new TrackingHttpHandler();
            field.SetValue(safeSender,new HttpClient(handler){BaseAddress=new Uri("https://example.invalid/")});
            var rejected=await safeSender.SendReplyOnceAsync("fixture-only","fixture",1);
            check(handler.Calls==1&&rejected.IsRetryable&&rejected.RetryAfterSeconds==120,"one-shot assistant replies expose the full Telegram cooldown without internal retry loops");
            handler.Code=System.Net.HttpStatusCode.InternalServerError;var uncertain=await safeSender.SendReplyOnceAsync("fixture-only","fixture",1);
            check(handler.Calls==2&&!uncertain.IsRetryable,"one-shot assistant replies never retry an ambiguous Telegram server error inside the HTTP client");
        }
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var chat=-1000000000000L-Random.Shared.Next(10000000,90000000);var chatText=chat.ToString();var referenceChat=(chat-1).ToString();
        var customer=new Customer{Id=Guid.NewGuid(),FullName="Accountant fixture",Mobile=Guid.NewGuid().ToString("N")[..18]};
        var setting=new TelegramAccountantLearningSetting{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow.AddHours(-1),IsLearningEnabled=true,IsAutoReplyEnabled=false};
        db.Customers.Add(customer);db.CustomerTelegramGroups.Add(new(){Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chatText,Title="Fixture",IsActive=true});db.TelegramAccountantLearningSettings.Add(setting);
        for(var i=0;i<3;i++)
        {
            db.TelegramAccountantLearningMessages.Add(new(){Id=Guid.NewGuid(),ChatId=referenceChat,MessageId=i*2+1,SenderTelegramUserId="700",MessageText="بسته بندی چطور انجام میشه نمونه "+i,CreatedAt=DateTime.UtcNow.AddHours(-3)});
            db.TelegramAccountantLearningMessages.Add(new(){Id=Guid.NewGuid(),ChatId=referenceChat,MessageId=i*2+2,ReplyToMessageId=i*2+1,SenderTelegramUserId="701",IsAccountant=true,MessageText="بسته بندی با محافظ انجام میشه",CreatedAt=DateTime.UtcNow.AddHours(-3).AddSeconds(1)});
        }
        void Stored(long id,string text,DateTime? time=null,bool staff=false,long? reply=null)=>db.TelegramAccountantLearningMessages.Add(new(){Id=Guid.NewGuid(),ChatId=chatText,MessageId=id,SenderTelegramUserId=staff?"701":"702",SenderUsername=staff?"AccountantZibashe":"fixture_customer",IsAccountant=staff,ReplyToMessageId=reply,MessageText=text,CreatedAt=time??DateTime.UtcNow.AddMinutes(-20)});
        Stored(80,"بسته بندی چطور انجام میشه؟",DateTime.UtcNow.AddHours(-25));
        Stored(90,"بسته بندی چطور انجام میشه؟",DateTime.UtcNow.AddHours(-2));
        Stored(100,"بسته بندی چطور انجام میشه؟");Stored(101,"بسته بندی چطور انجام میشه؟");
        Stored(102,"با محافظ انجام میشه",staff:true,reply:101);Stored(103,"شماره کارت را لطفا بفرستین؟");
        Stored(104,"بسته بندی چطور انجام میشه؟");await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,AccountantSender>();var transport=(AccountantSender)(object)sender;
        var generator=new AccountantGenerator();
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId="771234567"}),("_accountantReplacementService",generator)})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        MethodInfo Method(string name)=>typeof(TelegramWebhookController).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!;
        TelegramMessage Message(long id,string text,string? username="fixture_customer",bool bot=false)=>new(text,new(chat,"supergroup"),new(702,username,IsBot:bot),null,MessageId:id);
        Task<bool> Live(TelegramMessage message)=>(Task<bool>)Method("TryHandleAccountantReplacementQuestionAsync").Invoke(controller,[message,CancellationToken.None])!;
        Task Process(NotificationOutbox job)=>(Task)Method("ProcessAccountantQuestionAsync").Invoke(controller,[job,CancellationToken.None])!;
        Task<int> Backlog()=>(Task<int>)Method("QueueStoredAccountantQuestionsAsync").Invoke(controller,[CancellationToken.None])!;
        async Task<NotificationOutbox> Job(long id)=>await db.NotificationOutbox.SingleAsync(v=>v.Recipient==chatText&&v.Payload.Contains("\"MessageId\":"+id+","));
        async Task Toggle(string username)
        {
            var callback=new TelegramCallbackQuery("fixture",new(771234567,username),new TelegramMessage(null,new(771234567,"private"),null,null,MessageId:1),"invoiceadmin:assistant:toggle-replies");
            await (Task)Method("ToggleAccountantReplacementReplyAsync").Invoke(controller,[callback,CancellationToken.None])!;
        }
        var controls=new List<Guid>();
        try
        {
            await Toggle("fixture_customer");check(!setting.IsAutoReplyEnabled,"only the designated manager can enable accountant replacement replies");
            await Toggle("zahraa_frj");
            controls.AddRange(await db.NotificationOutbox.Where(v=>v.Channel=="AccountantReplyControl"&&v.EventType=="Activated"&&v.Payload.Contains(setting.Id.ToString())).Select(v=>v.Id).ToArrayAsync());
            check(setting.IsAutoReplyEnabled&&controls.Count==1&&transport.ReplyCalls==0,"enabling replacement records a backlog boundary without sending customer replies on the callback");
            await (Task)Method("ObserveAccountantHumanReplyAsync").Invoke(controller,[Message(106,"/admin",username:"AccountantZibashe"),CancellationToken.None])!;
            var marker=Message(105,"پرداخت بررسی شد",username:"AccountantZibashe") with{ReplyToMessage=Message(104,"بسته بندی چطور انجام میشه؟")};
            await (Task)Method("ObserveAccountantHumanReplyAsync").Invoke(controller,[marker,CancellationToken.None])!;
            check(await Backlog()==2&&await Backlog()==0,"activation queues stored unanswered questions once while excluding old, financial and accountant-answered messages");
            var first=await Job(100);await Process(first);await Process(first);
            check(transport.ReplyCalls==1&&transport.LastReplyTo==100&&first.Status==NotificationOutboxStatus.Processed,
                "an untagged stored question receives one threaded reply and replay cannot send it twice");
            var humanAnswered=await Job(104);await Process(humanAnswered);
            check(transport.ReplyCalls==1&&humanAnswered.Status==NotificationOutboxStatus.Processed,
                "a later human reply receipt suppresses queued automation even when the human text was financial");
            setting.IsLearningEnabled=false;await db.SaveChangesAsync();
            await Live(Message(200,"بسته بندی چطور انجام میشه؟"));await Live(Message(200,"بسته بندی چطور انجام میشه؟"));
            var live=await Job(200);
            check(live.Channel=="AccountantReplacement"&&await db.NotificationOutbox.CountAsync(v=>v.Id==live.Id)==1,
                "new untagged customer questions use the priority live queue independently of the learning toggle");
            foreach(var message in new[]{Message(201,"بدهی من چقدره؟"),Message(202,"بسته بندی چطور انجام میشه؟",bot:true),Message(203,"بسته بندی چطور انجام میشه؟","zahraa_frj")})await Live(message);
            check(!await db.NotificationOutbox.AnyAsync(v=>v.Recipient==chatText&&(v.Payload.Contains("\"MessageId\":201,")||v.Payload.Contains("\"MessageId\":202,")||v.Payload.Contains("\"MessageId\":203,"))),
                "financial questions, bot messages and accountant-manager messages never enter the reply queue");
            generator.BeforeAnswer=async()=>{setting.IsAutoReplyEnabled=false;await db.SaveChangesAsync();};
            await Process(live);
            check(transport.ReplyCalls==1&&live.Status==NotificationOutboxStatus.Pending,"stopping replacement during answer generation prevents the outgoing reply");
            generator.BeforeAnswer=null;setting.IsAutoReplyEnabled=true;await db.SaveChangesAsync();
            transport.Result=new(false,"429"){IsRetryable=true,RetryAfterSeconds=120};await Process(live);
            check(live.Status==NotificationOutboxStatus.Pending&&live.NextAttemptAt>=DateTime.UtcNow.AddSeconds(115)&&!live.Payload.Contains("AttemptedAtUtc\":\""),
                "a definite Telegram rate-limit rejection can retry after the requested cooldown without losing the question");
            transport.Result=new(true,MessageId:555);await Process(live);
            check(live.Status==NotificationOutboxStatus.Processed&&transport.LastReplyTo==200,"a safely retried live reply is delivered to the original customer message");
            await Live(Message(204,"بسته بندی چطور انجام میشه؟"));var unknown=await Job(204);transport.Result=new(false,"unknown response");await Process(unknown);var calls=transport.ReplyCalls;await Process(unknown);
            check(unknown.Status==NotificationOutboxStatus.Failed&&transport.ReplyCalls==calls,"unknown Telegram reply outcomes are retained for review instead of blindly sending duplicates");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(v=>v.Recipient==chatText||controls.Contains(v.Id)).ExecuteDeleteAsync();
            await db.TelegramAccountantLearningMessages.Where(v=>v.ChatId==chatText||v.ChatId==referenceChat).ExecuteDeleteAsync();
            await db.TelegramAccountantLearningSettings.Where(v=>v.Id==setting.Id).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(v=>v.CustomerId==customer.Id).ExecuteDeleteAsync();await db.Customers.Where(v=>v.Id==customer.Id).ExecuteDeleteAsync();
        }
    }
}

public sealed class AccountantGenerator:IAccountantReplacementService
{
    public bool IsEnabled=>true;
    public Func<Task>? BeforeAnswer;
    public async Task<AccountantReplacementResult> AnswerAsync(string question,IReadOnlyCollection<AccountantAnswerExample> examples,CancellationToken ct)
    {if(BeforeAnswer!=null)await BeforeAnswer();return new(true,"بسته بندی با محافظ انجام میشه");}
}
public class AccountantSender:DispatchProxy
{
    public int ReplyCalls;public long LastReplyTo;public TelegramSendResult Result=new(true,MessageId:123);
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="SendReplyOnceAsync"){ReplyCalls++;LastReplyTo=(long)args![2]!;return Task.FromResult(Result);}
        if(method.Name is "AnswerCallbackAsync" or "SendInlineKeyboardAsync")return Task.FromResult(new TelegramSendResult(true));
        throw new InvalidOperationException("Unexpected external operation: "+method.Name);
    }
}
