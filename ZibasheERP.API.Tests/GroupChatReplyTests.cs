using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class GroupChatReplyTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const string target="-1001551061874";const string link="https://t.me/c/1551061874/148790";
        check(GroupChatReplyLink.TryParse(link,target,out var messageId)&&messageId==148790,"chat reply resolves the exact supplied discussion message link");
        foreach(var bad in new[]{"https://t.me/c/1236168661/148790","https://t.me/c/1551061875/148790","https://evil.invalid/c/1551061874/148790",
            "https://t.me.evil.invalid/c/1551061874/148790","http://t.me/c/1551061874/148790","https://t.me/c/1551061874/0","https://t.me/c/1551061874/9999999999999","https://t.me/zibasheperfume/148790"})
            check(!GroupChatReplyLink.TryParse(bad,target,out _),"chat reply rejects a different destination or malformed link: "+bad);
        check(!GroupChatReplyLink.TryParse(link,"",out _)&&GroupChatReplyLink.Url("",1)=="","missing discussion configuration cannot become a standalone group send");
        using(var realSender=new TelegramMessageSender(Options.Create(new TelegramOptions{BotToken="synthetic-token"})))
        {
            var field=typeof(TelegramMessageSender).GetField("_httpClient",BindingFlags.Instance|BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(realSender)!).Dispose();var http=new GroupReplyHttp();field.SetValue(realSender,new HttpClient(http){BaseAddress=new Uri("https://example.invalid/")});
            await realSender.SendReplyOnceAsync(target,"متن تست <b>بدون HTML</b>",148790);
            using(var request=JsonDocument.Parse(http.LastBody!))
            {
                var payload=request.RootElement;check(payload.GetProperty("reply_parameters").GetProperty("message_id").GetInt64()==148790&&
                    !payload.GetProperty("reply_parameters").GetProperty("allow_sending_without_reply").GetBoolean()&&!payload.TryGetProperty("parse_mode",out _),
                    "native reply transport binds the exact original message and explicitly forbids fallback without a reply");
            }
            http.Status=HttpStatusCode.TooManyRequests;var rejected=await realSender.SendReplyOnceAsync(target,"تست",148790);
            check(http.Calls==2&&rejected.IsRetryable&&rejected.RetryAfterSeconds==120,"group chat replies expose definite 429 cooldown without repeating HTTP internally");
            http.Status=HttpStatusCode.InternalServerError;var uncertain=await realSender.SendReplyOnceAsync(target,"تست",148790);
            check(http.Calls==3&&!uncertain.IsRetryable,"unknown group-reply server outcomes cannot trigger an internal duplicate send");
        }
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=741471928,staff=1907823600,outsider=920001111;
        var options=new TelegramOptions{OwnerUserId=owner.ToString(),AdminChatId="synthetic-admin-group",SalesDiscussionChatId=target};
        var sender=DispatchProxy.Create<ITelegramMessageSender,GroupReplySender>();var transport=(GroupReplySender)(object)sender;transport.Staff=staff;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)!=null)field.SetValue(controller,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",options),("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        var ids=new List<Guid>();long source=Random.Shared.Next(10000000,90000000);
        async Task<object?> Call(string name,params object?[] args)
        {var task=(Task)typeof(TelegramWebhookController).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        async Task Select(string action,long actor=owner,string type="private")=>await Call("TryHandleGroupChatReplyCallbackAsync",
            new TelegramCallbackQuery("reply-test",new(actor,null),new TelegramMessage(null,new(actor,type),new(actor,null),null,MessageId:1),"groupchatreply:"+action),CancellationToken.None);
        async Task Input(string text,long actor=owner)=>await Call("TryHandleGroupChatReplyMessageAsync",new TelegramMessage(text,new(actor,"private"),new(actor,null),null,MessageId:++source),CancellationToken.None);
        async Task<NotificationOutbox> Queue(long actor=owner,string text="پاسخ آزمایشی")
        {
            await Select("start",actor);await Input(link,actor);await Input(text,actor);
            var button=transport.Buttons.Last(b=>b.StartsWith("groupchatreply:send:"));await Select(button["groupchatreply:".Length..],actor);
            var row=await db.NotificationOutbox.AsNoTracking().Where(n=>n.Channel=="AdminGroupChatReply"&&n.Recipient==actor.ToString()&&n.Payload.Contains(source.ToString()))
                .SingleAsync();ids.Add(row.Id);return row;
        }
        async Task<string> State(Guid id)=>JsonNode.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id)).Payload)!["State"]!.GetValue<string>();
        async Task<int?> Process(NotificationOutbox job)=>(int?)await Call("ProcessGroupChatReplyAsync",job,CancellationToken.None);
        try
        {
            await Call("SendInvoiceAdminMenuAsync",staff,null,CancellationToken.None,null);
            check(transport.Labels.Contains("💬 پاسخگویی گروه چت"),"accounting administrators see the new group chat reply option in their panel");
            await Select("start",outsider);check(transport.Texts.Last().Contains("فقط برای ادمین"),"ordinary customers cannot start group chat replies");
            await Select("start",owner,"supergroup");check(transport.Texts.Last().Contains("خصوصی"),"group chat reply drafts cannot start from a group callback");
            await Select("start");await Input("https://t.me/c/1236168661/148790");check(transport.Texts.Last().Contains("لینک معتبر"),"wizard refuses a channel link before accepting reply text");
            await Input(link);await Input(new string('x',4097));check(transport.Texts.Last().Contains("۴۰۹۶"),"oversized group reply stays private and cannot be confirmed");
            await Input("متن اول");var old=transport.Buttons.Last(b=>b.StartsWith("groupchatreply:send:"));await Input("متن اصلاح‌شده");
            var before=await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminGroupChatReply");
            await Select(old["groupchatreply:".Length..]);check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminGroupChatReply")==before,"editing a reply invalidates the old confirmation token");
            var cancelled=transport.Buttons.Last(b=>b.StartsWith("groupchatreply:send:"));await Select("cancel");await Select(cancelled["groupchatreply:".Length..]);
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminGroupChatReply")==before&&transport.PublicReplies.Count==0,"cancelled and stale previews cannot publish any group reply");
            await Select("start");await Input(link);await Input("پیش‌نمایش مقصد ثابت");
            var movedPreview=transport.Buttons.Last(b=>b.StartsWith("groupchatreply:send:"));options.SalesDiscussionChatId="-1001236168661";
            await Select(movedPreview["groupchatreply:".Length..]);
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminGroupChatReply")==before,"destination changes after preview cannot redirect confirmation to a different group");
            options.SalesDiscussionChatId=target;await Select("cancel");
            var good=await Queue(staff,"پاسخ نهایی @Rose_blanche");var confirm=transport.Buttons.Last(b=>b.StartsWith("groupchatreply:send:"));await Select(confirm["groupchatreply:".Length..],staff);
            check(await db.NotificationOutbox.CountAsync(n=>n.Id==good.Id)==1&&transport.PublicReplies.Count==0,"confirmed administrator reply creates one durable job without sending on the webhook");
            await Process(good);await Process(good);
            check(transport.PublicReplies.Count==1&&transport.PublicReplies[0]==(target,148790L,"پاسخ نهایی @Rose_blanche")&&await State(good.Id)=="sent",
                "worker replies on the selected original message exactly once with the approved administrator text");
            check(transport.Texts.Any(t=>t.Contains("https://t.me/c/1551061874/777")),"the originating admin receives the actual sent reply link privately");
            await Select("status:"+good.Id.ToString("N"),owner);check(transport.Texts.Last().Contains("متعلق به شما"),"another administrator cannot inspect someone else's private reply job");
            var deleted=await Queue();transport.Result=new(false,"Bad Request: message to be replied not found");var calls=transport.PublicReplies.Count;
            await Process(deleted);await Process(deleted);
            check(await State(deleted.Id)=="failed"&&transport.PublicReplies.Count==calls+1&&transport.Texts.Any(t=>t.Contains("پیام مستقلی در گروه ارسال نشد")),
                "deleted target produces an explicit failure and never falls back to a new standalone group message");
            var retry=await Queue();transport.Result=new(false,"429"){IsRetryable=true,RetryAfterSeconds=120};calls=transport.PublicReplies.Count;
            check(await Process(retry)==120&&await State(retry.Id)=="queued","definite Telegram rejection resets only the unsent attempt and preserves the full retry delay");
            transport.Result=new(true,MessageId:777);await Process(retry);await Process(retry);
            check(transport.PublicReplies.Count==calls+2&&await State(retry.Id)=="sent","safe 429 retry publishes one accepted reply and cannot duplicate it after completion");
            var unknown=await Queue();transport.Result=new(false,"network timeout");calls=transport.PublicReplies.Count;await Process(unknown);await Process(unknown);
            check(await State(unknown.Id)=="unknown"&&transport.PublicReplies.Count==calls+1,"uncertain HTTP outcome requires review and is not resent automatically");
            var interrupted=await Queue();var raw=JsonNode.Parse(interrupted.Payload)!;raw["AttemptedAt"]=JsonSerializer.SerializeToNode(DateTime.UtcNow);raw["State"]="sending";
            await db.NotificationOutbox.Where(n=>n.Id==interrupted.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,raw.ToJsonString()));calls=transport.PublicReplies.Count;await Process(interrupted);
            check(await State(interrupted.Id)=="unknown"&&transport.PublicReplies.Count==calls,"a crash after the durable attempt marker cannot cause a duplicate public reply");
            transport.Result=new(true,MessageId:777);var revoked=await Queue(staff);transport.Staff=0;calls=transport.PublicReplies.Count;await Process(revoked);
            check(await State(revoked.Id)=="failed"&&transport.PublicReplies.Count==calls,"worker rechecks staff authorization before sending a queued reply");transport.Staff=staff;
            var moved=await Queue();options.SalesDiscussionChatId="-1001236168661";calls=transport.PublicReplies.Count;await Process(moved);
            check(await State(moved.Id)=="failed"&&transport.PublicReplies.Count==calls,"changing the configured discussion destination cannot redirect an already approved reply");options.SalesDiscussionChatId=target;
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>ids.Contains(n.Id)).ExecuteDeleteAsync();await Select("cancel");await Select("cancel",staff);
        }
    }
}

public class GroupReplySender:DispatchProxy
{
    public long Staff;public TelegramSendResult Result=new(true,MessageId:777);
    public List<string> Texts=new(),Buttons=new(),Labels=new();public List<(string Chat,long Target,string Text)> PublicReplies=new();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="IsChatAdministratorAsync")return Task.FromResult((string)args![1]! == Staff.ToString());
        if(method.Name=="SendReplyOnceAsync")
        {
            var chat=(string)args![0]!;var text=(string)args[1]!;
            if(chat.StartsWith('-')){PublicReplies.Add((chat,(long)args[2]!,text));return Task.FromResult(Result);}
            Texts.Add(text);
        }
        if(method.Name=="AnswerCallbackAsync"&&args![1] is string answer)Texts.Add(answer);
        if(method.Name is "SendAsync" or "SendInlineKeyboardAsync")Texts.Add((string)args![1]!);
        foreach(var value in args??[])if(value is IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows)
        {Buttons.AddRange(rows.SelectMany(r=>r).Select(b=>b.CallbackData??""));Labels.AddRange(rows.SelectMany(r=>r).Select(b=>b.Text));}
        return Task.FromResult(new TelegramSendResult(true,MessageId:778));
    }
}

public class GroupReplyHttp:HttpMessageHandler
{
    public string? LastBody;public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Calls++;LastBody=await request.Content!.ReadAsStringAsync(ct);
        return new(Status){Content=new StringContent(Status==HttpStatusCode.OK?"{\"ok\":true,\"result\":{\"message_id\":777}}":"{\"ok\":false,\"description\":\"test rejection\",\"parameters\":{\"retry_after\":120}}",Encoding.UTF8,"application/json")};
    }
}
