using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Application.Services;
using ZibasheERP.Infrastructure.Persistence;

public static class PerfumePurchaseReportTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var eligible=typeof(TelegramWebhookController).GetMethod("PurchaseReportEligible",BindingFlags.NonPublic|BindingFlags.Static)!;
        bool Eligible(SalesList list,int threshold)=>(bool)eligible.Invoke(null,[list,threshold])!;
        var boundary=new SalesList{Status=SalesListStatus.Open,TotalVolume=100,ReservedVolume=90};
        check(!Eligible(boundary,10)&&Eligible(boundary,20),"purchase report uses strict cumulative percentage boundaries");
        boundary.TotalVolume=0;check(!Eligible(boundary,50),"zero-volume lists cannot enter purchase reports");
        boundary.TotalVolume=100;boundary.ReservedVolume=100;check(!Eligible(boundary,50),"completed zero-remainder lists cannot enter purchase reports");
        boundary.ReservedVolume=99;boundary.IsInventoryOffer=true;check(!Eligible(boundary,50),"ready inventory offers are excluded from perfume purchase reports");
        boundary.IsInventoryOffer=false;boundary.Status=SalesListStatus.Cancelled;check(!Eligible(boundary,50),"cancelled ordinary lists are excluded");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrEmpty(connection))throw new InvalidOperationException("Isolated SQL required");
        var selector=new SqlConnectionStringBuilder(connection);if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Development DB only");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=741471928,staff=1907823600;
        var sender=DispatchProxy.Create<ITelegramMessageSender,PurchaseReportSender>();var transport=(PurchaseReportSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)!=null)field.SetValue(controller,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_adminActivityContext",new AdminActivityContext()),("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString()})})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task<object?> Call(string name,params object?[] args){var task=(Task)typeof(TelegramWebhookController).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        async Task Select(string action,long actor=owner,string type="private")=>await Call("TryHandlePerfumePurchaseReportCallbackAsync",new TelegramCallbackQuery("test-purchase-report",new(actor,null),new TelegramMessage(null,new(actor,type),new(actor,null),null,MessageId:42),"purchase-report:"+action),CancellationToken.None);
        async Task<int?> Process(NotificationOutbox job)=>(int?)await Call("ProcessPerfumePurchaseReportAsync",job,CancellationToken.None);
        async Task<JsonNode> State(Guid id)=>JsonNode.Parse((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id)).Payload)!;
        var lists=new List<SalesList>();var jobs=new List<Guid>();var seed=Random.Shared.Next(7000000,8000000);var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();
        SalesList Add(decimal remaining,int index,string? photo="fixture-photo")
        {
            var list=new SalesList{Id=Guid.NewGuid(),PerfumeId=perfume,PublicCode=seed+index,PersianName="عطر تست خرید "+index,EnglishName="Purchase fixture "+index,
                Status=SalesListStatus.Open,TotalVolume=100,ReservedVolume=100-remaining,TelegramChannelId="@synthetic_purchase_report",TelegramMessageId=seed+index,
                TelegramPhotoFileId=photo,CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow};lists.Add(list);db.SalesLists.Add(list);return list;
        }
        try
        {
            await Call("SendInvoiceAdminMenuAsync",staff,null,CancellationToken.None,null);
            check(!transport.Buttons.Contains("purchase-report:menu"),"purchase report menu is hidden from accountant administrators");
            await Call("SendInvoiceAdminMenuAsync",owner,null,CancellationToken.None,null);
            check(transport.Buttons.Contains("purchase-report:menu"),"purchase report menu is visible to the configured primary owner");
            for(var i=0;i<105;i++)Add(9,i);
            var exact=Add(10,106);var twenty=Add(20,107);var thirty=Add(30,108);var fifty=Add(50,109);
            var missing=Add(5,110,null);missing.EnglishName="Unique missing-photo "+seed;
            var stock=Add(1,111);stock.IsInventoryOffer=true;var full=Add(0,112);full.Status=SalesListStatus.Full;
            var higher=Add(40,114);
            await db.SaveChangesAsync();
            var before=await db.NotificationOutbox.CountAsync(n=>n.Channel=="PerfumePurchaseReport");
            await Select("10",staff);await Select("10",owner,"supergroup");await Select("99");
            check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="PerfumePurchaseReport")==before,"purchase report rejects accountant, group context and forged thresholds before queuing");
            await Select("menu");check(new[]{10,20,30,50}.All(n=>transport.Buttons.Contains("purchase-report:"+n)),"owner sees all four purchase thresholds");
            await Select("10");var job=await db.NotificationOutbox.AsNoTracking().Where(n=>n.Channel=="PerfumePurchaseReport"&&n.Status==NotificationOutboxStatus.Pending).SingleAsync();jobs.Add(job.Id);
            var ids=(await State(job.Id))["Lists"]!.AsArray().Select(x=>Guid.Parse(x!.GetValue<string>())).ToArray();
            check(lists.Take(105).All(l=>ids.Contains(l.Id))&&ids.Contains(missing.Id),"purchase report includes over one hundred eligible lists without a result cap");
            check(!ids.Contains(exact.Id)&&!ids.Contains(stock.Id)&&!ids.Contains(full.Id)&&!ids.Contains(higher.Id),"SQL report excludes exact boundary, stock, full and higher remainder lists");
            await Select("10");check(await db.NotificationOutbox.CountAsync(n=>n.Channel=="PerfumePurchaseReport")==before+1,"duplicate clicks cannot queue overlapping private reports");
            transport.Result=new(false,"429"){IsRetryable=true,RetryAfterSeconds=120};check(await Process(job)==120&&(await State(job.Id))["Next"]!.GetValue<int>()==0,"purchase report preserves position and full Telegram cooldown on 429");
            transport.Result=new(true,MessageId:777);await Process(job);
            check(transport.Deliveries.Last().Chat==owner.ToString()&&transport.Deliveries.Last().Text.Contains("باقی‌مانده:")&&transport.Deliveries.Last().Text.Contains("https://t.me/"),"report card is private to numeric owner with quantities and an exact clickable post link");
            var nextId=ids[(await State(job.Id))["Next"]!.GetValue<int>()];await db.SalesLists.Where(l=>l.Id==nextId).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.Status,SalesListStatus.Full));
            var count=transport.Deliveries.Count;await Process(job);check(transport.Deliveries.Count==count&&(await State(job.Id))["Skipped"]!.GetValue<int>()==1,"lists completed during report are revalidated and skipped before delivery");
            for(var i=0;i<ids.Length+2;i++)if(await Process(job) is null)break;
            check((await State(job.Id))["Finished"]!.GetValue<bool>()&&transport.Deliveries.Count>100,"durable stream automatically reaches all report cards and final summary");
            check(transport.Deliveries.Any(d=>d.Text.Contains(missing.PersianName)&&d.Text.Contains("عکس ثبت‌شده")),"missing photo does not silently discard an eligible list");
            var done=transport.Deliveries.Count;await Process(job);check(transport.Deliveries.Count==done,"finished report cannot duplicate its summary");
            await db.NotificationOutbox.Where(n=>n.Id==job.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            foreach(var threshold in new[]{20,30,50})
            {
                await Select(threshold.ToString());var row=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Channel=="PerfumePurchaseReport"&&n.Status==NotificationOutboxStatus.Pending);jobs.Add(row.Id);
                var selected=(await State(row.Id))["Lists"]!.AsArray().Select(x=>Guid.Parse(x!.GetValue<string>())).ToArray();
                check(selected.Contains(exact.Id)&&selected.Contains(twenty.Id)==(threshold>20)&&selected.Contains(thirty.Id)==(threshold>30)&&!selected.Contains(fifty.Id),"cumulative SQL threshold is strict for "+threshold+" percent");
                var node=await State(row.Id);node["Attempted"]=true;await db.NotificationOutbox.Where(n=>n.Id==row.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,node.ToJsonString()));
                var sentBefore=transport.Deliveries.Count;await Process(row);check(transport.Deliveries.Count==sentBefore&&(await State(row.Id))["Failed"]!.GetValue<int>()==1,"unknown in-flight send after restart cannot duplicate a report card");
                node=await State(row.Id);node["Actor"]=staff;await db.NotificationOutbox.Where(n=>n.Id==row.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,node.ToJsonString()));
                check(await Process(row) is null&&transport.Deliveries.Count==sentBefore,"worker independently rejects forged non-owner report state");
                await db.NotificationOutbox.Where(n=>n.Id==row.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            }
        }
        finally
        {
            await db.NotificationOutbox.Where(n=>jobs.Contains(n.Id)).ExecuteDeleteAsync();var ids=lists.Select(l=>l.Id).ToArray();await db.SalesLists.Where(l=>ids.Contains(l.Id)).ExecuteDeleteAsync();
        }
    }
}

public class PurchaseReportSender:DispatchProxy
{
    public List<string> Buttons=new();public List<(string Chat,string Text)> Deliveries=new();public TelegramSendResult Result=new(true,MessageId:777);
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        foreach(var value in args??[])if(value is IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows)Buttons.AddRange(rows.SelectMany(r=>r).Select(b=>b.CallbackData??""));
        if(method!.Name=="SendPhotoOnceWithKeyboardAsync") {Deliveries.Add(((string)args![0]!, (string)args[2]!));return Task.FromResult(Result);}
        if(method.Name=="SendReplyOnceAsync") {Deliveries.Add(((string)args![0]!, (string)args[1]!));return Task.FromResult(Result);}
        return Task.FromResult(new TelegramSendResult(true,MessageId:778));
    }
}
