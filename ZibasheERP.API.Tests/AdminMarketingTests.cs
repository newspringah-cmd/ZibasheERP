using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class AdminMarketingTests
{
    private const long Owner = 771234567;
    private static TelegramWebhookController Controller(ITelegramMessageSender sender, AppDbContext? db = null)
    {
        var c=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts",StringComparison.Ordinal) && field.FieldType.GetConstructor(Type.EmptyTypes) is not null)
                field.SetValue(c,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new (string,object?)[] {("_sender",sender),("_db",db),
            ("_options",new TelegramOptions { OwnerUserId=Owner.ToString(),AdminChatId="-100123" }),
            ("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(c,value);
        return c;
    }
    private static Task Invoke(TelegramWebhookController c,string method,params object?[] args) =>
        (Task)typeof(TelegramWebhookController).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,args)!;
    private static TelegramMessage Message(string? text,long id=1,long user=Owner) => new(text,new(user,"private"),new(user,null),null,MessageId:id);
    private static Task Select(TelegramWebhookController c,string action,long user=Owner) =>
        Invoke(c,"HandleMarketingCallbackAsync",new TelegramCallbackQuery("test",new(user,null),Message(null,user:user),"invoiceadmin:marketing:"+action),CancellationToken.None);
    private static async Task<bool> Input(TelegramWebhookController c,string text,long id=1,long user=Owner) =>
        await (Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleMarketingMessageAsync",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(c,[Message(text,id,user),CancellationToken.None])!;

    private static Task AutoCopy(TelegramWebhookController c,MarketingSender sent) => Select(c,sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-mode:",StringComparison.Ordinal)&&v.EndsWith(":ziba",StringComparison.Ordinal))["invoiceadmin:marketing:".Length..]);

    public static async Task Run(Action<bool,string> check)
    {
        var sender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var sent=(MarketingSender)(object)sender;
        var c=Controller(sender);
        await Invoke(c,"SendInvoiceAdminMenuAsync",Owner,null,CancellationToken.None,null);
        check(sent.Buttons.Contains("invoiceadmin:marketing:menu"),"admin main menu exposes advertising entry");
        check(sent.Buttons.Contains("invoiceadmin:marketing:group-message"),"admin main menu exposes the group message composer");
        check(sent.Buttons.Contains("invoiceadmin:flash:menu"),"owner main menu exposes hourly promotion");
        sent.AllowAccountingAdmin=true;
        var buttonOffset=sent.Buttons.Count;
        await Invoke(c,"SendInvoiceAdminMenuAsync",Owner+1,null,CancellationToken.None,null);
        await Select(c,"menu",Owner+1);
        check(!sent.Buttons.Skip(buttonOffset).Any(v=>v.Contains("group-message") || v.StartsWith("invoiceadmin:flash:")),
            "legitimate accounting administrator cannot see owner message or hourly controls in either menu");
        await Select(c,"group-message",Owner+1);
        check(sent.Texts.Last().Contains("فقط برای مدیر"),"old group composer callback remains denied to accounting administrator");
        await Invoke(c,"HandleFlashCallbackAsync",new TelegramCallbackQuery("test",new(Owner+1,null),Message(null,user:Owner+1),"invoiceadmin:flash:new"),CancellationToken.None);
        check(sent.Texts.Last().Contains("فقط برای مدیر"),"forged hourly callback cannot grant accounting administrator access");
        sent.AllowAccountingAdmin=false;
        await Select(c,"group-message");
        var draftText="سلام <b>دوستان</b>\nخط دوم 🌸";
        check(await Input(c,draftText,401) && sent.Texts.Last()==draftText,
            "group composer previews the exact plain text without posting or creating a job");
        var oldConfirm=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:group-message-send:",StringComparison.Ordinal));
        await Input(c,"متن اصلاح‌شده",402);
        var confirm=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:group-message-send:",StringComparison.Ordinal));
        await Select(c,oldConfirm["invoiceadmin:marketing:".Length..]);
        check(sent.Texts.Last().Contains("منقضی"),"an older preview cannot send a newer administrator message");
        await Select(c,confirm["invoiceadmin:marketing:".Length..],Owner+1);
        check(sent.Texts.Last().Contains("دسترسی"),"group message confirmation rechecks administrator authorization");
        await Input(c,new string('x',4097),403);
        check(sent.Texts.Last().Contains("۴۰۹۶"),"group composer rejects oversized messages before confirmation");
        await Input(c,"لغو",404);
        await Select(c,confirm["invoiceadmin:marketing:".Length..]);
        check(sent.Texts.Last().Contains("منقضی"),"cancelling a group message invalidates its confirmation button");
        await Select(c,"menu");
        check(sent.Buttons.Contains("invoiceadmin:marketing:group") && sent.Buttons.Contains("invoiceadmin:marketing:channel"),"advertising submenu offers group and channel destinations");
        check(sent.Buttons.Contains("invoiceadmin:marketing:delete-channel"),"advertising menu exposes channel removal");
        await Select(c,"delete-channel");
        check(sent.Buttons.Contains("invoiceadmin:marketing:delete-channel:confirm") && sent.Texts.Last().Contains("متوقف"),
            "channel removal requires explicit confirmation and explains the automation pause");
        check(!await Input(c,"9056"),"opening removal confirmation cannot queue a perfume-code advertisement");
        await Select(c,"delete-channel:confirm",Owner+1);
        check(sent.Texts.Last().Contains("دسترسی"),"crafted removal confirmation is denied for a nonadministrator");
        await Select(c,"group",Owner+1);
        check(sent.Texts.Last().Contains("دسترسی"),"unapproved Telegram user cannot start advertising even with a crafted callback");
        check(!await Input(c,"9056",user:Owner+1),"unauthorized callback leaves no code-entry draft");
        await Select(c,"group");await Input(c,"not-a-code");
        check(sent.Texts.Last().Contains("کد عددی"),"invalid list code keeps the admin in code entry without touching the database");
        await Select(c,"menu");
        check(!await Input(c,"9056"),"returning to advertising menu cancels stale numeric input");
        await Select(c,"channel");
        check(sent.Texts.Last().Contains("۲۴ ساعت"),"channel promotion shows its 24-hour duration before code entry");
        await Input(c,"لغو");check(!await Input(c,"9056"),"cancel removes an unfinished promotion draft");
        check(MarketingPromotionJob.RequestId(1,2,3)==MarketingPromotionJob.RequestId(1,2,3) &&
            MarketingPromotionJob.RequestId(1,2,3)!=MarketingPromotionJob.RequestId(1,2,4),"Telegram message identity makes request IDs stable across redelivery");
        check(typeof(MarketingPromotionsController).GetCustomAttribute<AuthorizeAttribute>()?.Roles=="Admin", "queue dispatch endpoints require Admin API authentication");
        check(MarketingPromotionsController.ValidResultUrl("https://t.me/c/1283018740/7","group") &&
            MarketingPromotionsController.ValidResultUrl("https://t.me/zibasheperfume/7","channel") &&
            !MarketingPromotionsController.ValidResultUrl("https://t.me/other/7","channel") &&
            !MarketingPromotionsController.ValidResultUrl("https://t.me/c/1283018740/7?x=1","group"),"result receipts are limited to the configured group or channel message URLs");
        var repo=DispatchProxy.Create<INotificationOutboxRepository,MarketingRepository>();var store=(MarketingRepository)(object)repo;
        store.Job=new NotificationOutbox { Id=Guid.NewGuid(),Channel=MarketingPromotionJob.Channel,Status=NotificationOutboxStatus.Processing,
            Payload=JsonSerializer.Serialize(new MarketingPromotionJob(Guid.NewGuid(),42,"group",Owner,Owner)) };
        var api=new MarketingPromotionsController(repo,sender);var job=store.Job;
        var before=sent.Texts.Count;
        check(await api.Result(job.Id,new("sent","https://t.me/other/7"),CancellationToken.None) is BadRequestResult && job.Status==NotificationOutboxStatus.Processing,
            "a foreign destination receipt cannot mark a promotion delivered");
        await api.Result(job.Id,new("sent","https://t.me/c/1283018740/7"),CancellationToken.None);
        check(job.Status==NotificationOutboxStatus.Processed && JsonSerializer.Deserialize<MarketingPromotionJob>(job.Payload)!.ResultUrl is not null && sent.Texts.Count==before+1,
            "accepted promotion result persists its link and notifies the requesting admin");
        await api.Result(job.Id,new("sent","https://t.me/c/1283018740/7"),CancellationToken.None);
        check(sent.Texts.Count==before+1,"replayed result acknowledgement does not repeat the admin notification");
        job.Status=NotificationOutboxStatus.Processing;job.Attempts=0;
        await api.Result(job.Id,new("retry",Error:"temporary"),CancellationToken.None);
        check(job.Status==NotificationOutboxStatus.Pending && job.NextAttemptAt>DateTime.UtcNow && sent.Texts.Count==before+1,"safe preparation failure retries without false success or repeated admin messages");
        job.Status=NotificationOutboxStatus.Processing;
        await api.Result(job.Id,new("unknown"),CancellationToken.None);
        check(job.Status==NotificationOutboxStatus.Failed && job.NextAttemptAt is null && job.LastError!.Contains("نامشخص"),"ambiguous publication is terminal until reviewed and cannot blindly retry");
        job.Channel="Telegram";job.Status=NotificationOutboxStatus.Processing;
        check(await api.Result(job.Id,new("sent","https://t.me/c/1283018740/7"),CancellationToken.None) is NotFoundResult,"marketing result endpoint cannot alter customer notification jobs");
        job.Channel=MarketingPromotionJob.Channel;job.Status=NotificationOutboxStatus.Processing;
        job.Payload=JsonSerializer.Serialize(new MarketingPromotionJob(Guid.Empty,0,MarketingPromotionJob.RemoveChannel,Owner,Owner));
        check(await api.Result(job.Id,new("sent","https://t.me/c/1283018740/7"),CancellationToken.None) is BadRequestResult,
            "removal cannot be falsely acknowledged with a publication receipt");
        before=sent.Texts.Count;
        await api.Result(job.Id,new("sent"),CancellationToken.None);
        check(job.Status==NotificationOutboxStatus.Processed && sent.Texts.Last().Contains("حذف شدند") && sent.Texts.Last().Contains("متوقف"),
            "successful removal needs no message URL and reports the paused channel state");
        await api.Result(job.Id,new("sent"),CancellationToken.None);
        check(sent.Texts.Count==before+1,"replayed removal acknowledgement does not repeat its notification");
        store.Job=new NotificationOutbox { Id=Guid.NewGuid(),Channel=MarketingPromotionJob.Channel,Status=NotificationOutboxStatus.Processing,
            Payload=JsonSerializer.Serialize(new MarketingPromotionJob(Guid.Empty,0,MarketingPromotionJob.GroupMessage,Owner,Owner,MessageText:"fixture")) };
        check(await api.Result(store.Job.Id,new("sent","https://t.me/zibasheperfume/7"),CancellationToken.None) is BadRequestResult,
            "group message delivery cannot accept a channel receipt");
        await api.Result(store.Job.Id,new("sent","https://t.me/c/1283018740/7"),CancellationToken.None);
        check(store.Job.Status==NotificationOutboxStatus.Processed && sent.Texts.Last().Contains("پیام شما"),
            "delivered group messages persist their receipt and notify the requesting administrator");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if(!string.IsNullOrWhiteSpace(connection)) await Sql(connection,sender,sent,check);
    }

    private static async Task Sql(string connection,ITelegramMessageSender sender,MarketingSender sent,Action<bool,string> check)
    {
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev" || selector.DataSource!="127.0.0.1,14333") throw new InvalidOperationException("Marketing tests require the isolated dev database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var list=new SalesList { Id=Guid.NewGuid(),PerfumeId=await db.Perfumes.Select(v=>v.Id).FirstAsync(),PublicCode=Random.Shared.Next(1000000,2000000),
            EnglishName="Marketing fixture",PersianName="عطر آزمایشی",DisplayBrand="Fixture",TotalVolume=100,ReservedVolume=86,
            Status=SalesListStatus.Open,Gender=PerfumeGender.Unisex,OpenDate=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,
            TelegramChannelId="-1001236168661",TelegramMessageId=7,TelegramPhotoFileId="fixture-photo" };
        db.SalesLists.Add(list);await db.SaveChangesAsync();
        var c=Controller(sender,db);var marker=list.Id.ToString();
        try
        {
            await Select(c,"group");
            var persian=string.Concat(list.PublicCode.ToString().Select(ch=>"۰۱۲۳۴۵۶۷۸۹"[ch-'0']));
            await Input(c,persian,301);
            check(!await db.NotificationOutbox.AnyAsync(v=>v.Channel==MarketingPromotionJob.Channel&&v.Payload.Contains(marker)),"entering perfume code waits for auto or admin-copy choice without publishing");
            await AutoCopy(c,sent);
            var jobs=await db.NotificationOutbox.Where(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker)).ToArrayAsync();
            check(jobs.Length==1 && jobs[0].Status==NotificationOutboxStatus.Pending && JsonSerializer.Deserialize<MarketingPromotionJob>(jobs[0].Payload)!.Destination=="group",
                "Persian numeric code queues an exact open-list group promotion in SQL");
            check(jobs[0].CustomerId==Guid.Empty,"administrator advertising does not create or modify a customer account");
            await Select(c,"group");await Input(c,persian,301);await AutoCopy(c,sent);
            await Select(c,"group");await Input(c,persian,302);await AutoCopy(c,sent);
            check(await db.NotificationOutbox.CountAsync(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker))==1,
                "redelivered code and double input reuse the pending job instead of publishing twice");
            await Select(c,"channel");await Input(c,persian,303);await AutoCopy(c,sent);
            check(await db.NotificationOutbox.CountAsync(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker))==1,
                "channel code asks keep-or-replace before creating a publication job");
            var stale=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:channel-mode:",StringComparison.Ordinal) && v.EndsWith(":append",StringComparison.Ordinal));
            await Input(c,persian,305);
            await Select(c,stale["invoiceadmin:marketing:".Length..]);
            check(sent.Texts.Last().Contains("منقضی"),"outdated channel-choice buttons cannot publish a newer entered code");
            await AutoCopy(c,sent);
            var choice=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:channel-mode:",StringComparison.Ordinal) && v.EndsWith(":append",StringComparison.Ordinal));
            await Select(c,choice["invoiceadmin:marketing:".Length..]);
            jobs=await db.NotificationOutbox.Where(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker)).ToArrayAsync();
            check(jobs.Length==2 && jobs.Any(v=>JsonSerializer.Deserialize<MarketingPromotionJob>(v.Payload) is {Destination:"channel",HoldHours:24,ChannelMode:"append"}),
                "the same code can independently request a 24-hour channel advertisement");
            await Select(c,"channel");await Input(c,persian,306);await AutoCopy(c,sent);
            var replacement=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:channel-mode:",StringComparison.Ordinal) && v.EndsWith(":replace",StringComparison.Ordinal));
            await Select(c,replacement["invoiceadmin:marketing:".Length..]);
            check(await db.NotificationOutbox.CountAsync(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker))==3,
                "a later replace choice for the same perfume is distinct from its earlier append request");
            await Select(c,"delete-channel:confirm");
            var removalId=MarketingPromotionJob.RemovalRequestId(Owner,Owner,"test");
            var removal=await db.NotificationOutbox.SingleAsync(v=>v.Id==removalId);
            var removalPayload=JsonSerializer.Deserialize<MarketingPromotionJob>(removal.Payload)!;
            check(removal.EventType=="ChannelPromotionRemoval" && removalPayload.Destination==MarketingPromotionJob.RemoveChannel &&
                removalPayload.SalesListId==Guid.Empty && removalPayload.Code==0,
                "confirmed removal queues a control action without needing a stock list");
            await Select(c,"delete-channel:confirm");
            check(await db.NotificationOutbox.CountAsync(v=>v.Id==removalId)==1,"repeated removal confirmation reuses its durable job");
            list.Status=SalesListStatus.Full;await db.SaveChangesAsync();
            await Select(c,"group");await Input(c,persian,304);
            check(sent.Texts.Last().Contains("تکمیل‌شده") && await db.NotificationOutbox.CountAsync(v=>v.Payload.Contains(marker))==3,
                "completed lists do not add an advertising job");
            await Select(c,"group-message");
            var body="پیام آزمایشی با خط جدید\n"+marker;
            await Input(c,body,450);
            var messageId=MarketingPromotionJob.GroupMessageRequestId(Owner,Owner,450);
            check(!await db.NotificationOutbox.AnyAsync(v=>v.Id==messageId),"group message preview does not enqueue a publication before approval");
            var send=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:group-message-send:",StringComparison.Ordinal));
            await Select(c,send["invoiceadmin:marketing:".Length..]);
            await Select(c,send["invoiceadmin:marketing:".Length..]);
            var queued=await db.NotificationOutbox.SingleAsync(v=>v.Id==messageId);
            var queuedPayload=JsonSerializer.Deserialize<MarketingPromotionJob>(queued.Payload)!;
            check(queued.EventType=="AdminGroupMessage" && queued.CustomerId==Guid.Empty && queuedPayload.SalesListId==Guid.Empty &&
                queuedPayload.Destination==MarketingPromotionJob.GroupMessage && queuedPayload.MessageText==body,
                "approved group messages queue their exact text independently of perfume stock or customer accounts");
            check(await db.NotificationOutbox.CountAsync(v=>v.Id==messageId)==1,
                "repeated message confirmation cannot enqueue a second publication");
            list.Status=SalesListStatus.Open;await db.SaveChangesAsync();
            await Select(c,"group");await Input(c,persian,501);
            var oldMode=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-mode:")&&v.EndsWith(":own"));
            await Input(c,persian,502);
            await Select(c,oldMode["invoiceadmin:marketing:".Length..]);
            check(sent.Texts.Last().Contains("منقضی"),"old copy-mode button cannot act on a replacement code draft");
            var own=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-mode:")&&v.EndsWith(":own"));
            await Select(c,own["invoiceadmin:marketing:".Length..]);
            var custom="سلام <b>عطر دلخواه</b>\nهمین متن ادمین 🌸";
            await Input(c,custom,503);
            var oldSend=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-send:"));
            check(!await db.NotificationOutbox.AnyAsync(v=>v.Id==MarketingPromotionJob.RequestId(Owner,Owner,503)),"admin advertising copy is previewed before a group job is created");
            await Input(c,custom+" اصلاح",504);
            await Select(c,oldSend["invoiceadmin:marketing:".Length..]);
            check(sent.Texts.Last().Contains("منقضی"),"editing proposed group copy invalidates its old send button");
            var sendCopy=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-send:"));
            await Select(c,sendCopy["invoiceadmin:marketing:".Length..]);
            var customJob=await db.NotificationOutbox.SingleAsync(v=>v.Id==MarketingPromotionJob.RequestId(Owner,Owner,504));
            check(JsonSerializer.Deserialize<MarketingPromotionJob>(customJob.Payload)!.CustomText==custom+" اصلاح","group promotion preserves exact approved admin copy separately from auto-copy jobs");
            await Select(c,"channel");await Input(c,persian,505);
            own=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-mode:")&&v.EndsWith(":own"));
            await Select(c,own["invoiceadmin:marketing:".Length..]);await Input(c,new string('x',701),506);
            check(sent.Texts.Last().Contains("700"),"channel custom copy rejects text exceeding photo caption budget without queueing");
            await Input(c,"متن کانال اختصاصی",507);
            var channelChoice=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:channel-mode:")&&v.EndsWith(":append"));
            await Select(c,channelChoice["invoiceadmin:marketing:".Length..]);
            var channelJob=await db.NotificationOutbox.SingleAsync(v=>v.Id==MarketingPromotionJob.RequestId(Owner,Owner,507));
            check(JsonSerializer.Deserialize<MarketingPromotionJob>(channelJob.Payload) is {CustomText:"متن کانال اختصاصی",ChannelMode:"append",Destination:"channel"},"channel keep-or-replace choice preserves admin-provided promotional copy");
            await Select(c,"group");await Input(c,persian,509);
            own=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-mode:")&&v.EndsWith(":own"));
            await Select(c,own["invoiceadmin:marketing:".Length..]);await Input(c,new string('س',3500),510);
            sendCopy=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:marketing:copy-send:"));
            await Select(c,sendCopy["invoiceadmin:marketing:".Length..]);
            check(JsonSerializer.Deserialize<MarketingPromotionJob>((await db.NotificationOutbox.SingleAsync(v=>v.Id==MarketingPromotionJob.RequestId(Owner,Owner,510))).Payload)!.CustomText!.Length==3500,
                "maximum-length Persian custom copy queues without SQL pattern-length errors");


        }
        finally
        {
            await db.NotificationOutbox.Where(v=>v.Channel==MarketingPromotionJob.Channel && v.Payload.Contains(marker)).ExecuteDeleteAsync();
            var removalId=MarketingPromotionJob.RemovalRequestId(Owner,Owner,"test");
            await db.NotificationOutbox.Where(v=>v.Id==removalId && v.Channel==MarketingPromotionJob.Channel).ExecuteDeleteAsync();
            await db.SalesLists.Where(v=>v.Id==list.Id).ExecuteDeleteAsync();
            await Select(c,"menu");
        }
    }
}

public class MarketingSender:DispatchProxy
{
    public List<(string Chat,long Message)> Deletes {get;}=[];
    public TelegramSendResult DeleteResult {get;set;}=new(true);
    public TelegramSendResult EditResult {get;set;}=new(true);
    public List<(string Chat,long Message,string Text,int Buttons)> Edits {get;}=[];
    public bool AllowAccountingAdmin {get;set;}
    public List<string> Photos {get;}=[];
    public List<string> ButtonLabels {get;}=[];
    public List<(string Chat,long Post)> SourcePhotos {get;}=[];
    public string? RecoveredSourcePhoto {get;set;}="retrieved-source-photo";
    public List<string> Texts {get;}=[];
    public List<string> Buttons {get;}=[];
    public List<string> Urls {get;}=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name is "DeleteMessageAsync" or "DeleteMessageOnceAsync")
        {Deletes.Add(((string)args![0]!, (long)args[1]!));return Task.FromResult(DeleteResult);}
        if(method.Name is "EditCaptionWithKeyboardAsync" or "EditTextWithKeyboardAsync")
        {Edits.Add(((string)args![0]!, (long)args[1]!, (string)args[2]!, ((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!).Sum(r=>r.Count)));return Task.FromResult(EditResult);}
        if(method!.Name=="ReadSourcePhotoAsync")
        {SourcePhotos.Add(((string)args![1]!, (long)args[2]!));return Task.FromResult(new TelegramSendResult(true,MessageId:123,ExternalFileId:RecoveredSourcePhoto));}
        if(method!.Name=="IsChatAdministratorAsync") return Task.FromResult(AllowAccountingAdmin);
        if(method.Name=="AnswerCallbackAsync") {Texts.Add(args![1]?.ToString()??"");return Task.FromResult(new TelegramSendResult(true,null));}
        if(method.Name is "SendInlineKeyboardAsync" or "SendAsync" or "SendHtmlAsync" or "SendPhotoWithKeyboardAsync")
        {
            if(method.Name=="SendPhotoWithKeyboardAsync") Photos.Add((string)args![1]!);
            var textIndex=method.Name=="SendPhotoWithKeyboardAsync"?2:1;
            Texts.Add((string)args![textIndex]!);
            if(method.Name is "SendInlineKeyboardAsync" or "SendPhotoWithKeyboardAsync")
                foreach(var row in (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[textIndex+1]!)
                    foreach(var button in row) { Buttons.Add(button.CallbackData??"");ButtonLabels.Add(button.Text);Urls.Add(button.Url??""); }
            return Task.FromResult(new TelegramSendResult(true,null));
        }
        throw new NotSupportedException(method.Name);
    }
}
public class MarketingRepository:DispatchProxy
{
    public NotificationOutbox Job=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args) => method!.Name switch
    {
        "GetByIdAsync" => Task.FromResult<NotificationOutbox?>(Job),
        "SaveChangesAsync" => Task.CompletedTask,
        _ => throw new NotSupportedException(method.Name)
    };
}
