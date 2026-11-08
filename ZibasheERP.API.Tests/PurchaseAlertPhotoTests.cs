using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class PurchaseAlertPhotoTests
{
    private static TelegramWebhookController Controller(ITelegramMessageSender sender, ISalesListRepository repo, AppDbContext? db = null)
    {
        var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach (var (name,value) in new (string,object?)[] {
            ("_sender",sender),("_salesListRepository",repo),("_db",db),
            ("_options",new TelegramOptions { LowStockAlertChatId="purchase-group", PromotionAlertChatId="promotion-group" }),
            ("_logger",NullLogger<TelegramWebhookController>.Instance) })
            typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,value);
        return controller;
    }
    private static Task Alert(TelegramWebhookController controller, SalesList list) =>
        (Task)typeof(TelegramWebhookController).GetMethod("SendRemainingVolumeAlertsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(controller,[list,CancellationToken.None])!;
    private static SalesList List() => new() { Id=Guid.NewGuid(), PublicCode=42, EnglishName="Perfume <exact>",
        TotalVolume=100,ReservedVolume=80,TelegramChannelId="-100123",TelegramMessageId=7,TelegramPhotoFileId="exact-perfume-photo" };

    public static async Task Run(Action<bool,string> check)
    {
        var sender=DispatchProxy.Create<ITelegramMessageSender,PurchasePhotoSender>();var transport=(PurchasePhotoSender)(object)sender;
        var repo=DispatchProxy.Create<ISalesListRepository,PurchasePhotoRepository>();var store=(PurchasePhotoRepository)(object)repo;
        var controller=Controller(sender,repo);var list=List();
        await Alert(controller,list);
        check(transport.Calls.Count==1 && transport.Calls[0].Chat=="purchase-group" && transport.Calls[0].Photo=="exact-perfume-photo",
            "purchase threshold alert always sends the exact perfume photo to the buying group");
        check(transport.Calls[0].Caption.Contains("&lt;exact&gt;") && transport.Calls[0].Caption.Contains("20") && transport.Calls[0].Caption.Contains("https://t.me/"),
            "photo alert retains escaped perfume name, live remainder and original channel link");
        check(list.LowStockAlertSentAt.HasValue && list.PromotionAlertSentAt is null && store.Saves==1,
            "only an accepted purchase photo sets its delivery marker");
        await Alert(controller,list);
        check(transport.Calls.Count==1,"already delivered purchase photo is not repeated");
        list.ReservedVolume=95;await Alert(controller,list);
        check(transport.Calls.Count==2 && transport.Calls[1].Chat=="promotion-group" && transport.Calls[1].Photo=="exact-perfume-photo",
            "promotion threshold also carries the perfume image without repeating the purchase alert");
        transport.Calls.Clear();store.Saves=0;transport.Success=false;
        list=List();list.ReservedVolume=95;await Alert(controller,list);
        check(list.LowStockAlertSentAt is null && list.PromotionAlertSentAt is null && store.Saves==0,
            "failed photo delivery never records a false successful purchase or promotion alert");
        transport.Success=true;await Alert(controller,list);
        check(list.LowStockAlertSentAt.HasValue && list.PromotionAlertSentAt.HasValue && transport.Calls.Count==4,
            "unaccepted photo alerts can be retried on the next list refresh");
        transport.Calls.Clear();list.TelegramPhotoFileId=null;await Alert(controller,list);
        check(transport.Calls.Count==0,"completed alert markers need no picture lookup or duplicate send");
        check(await SalesListPhotoResolver.ResolveAsync(null!,List(),CancellationToken.None)=="exact-perfume-photo",
            "the current list photo has priority and needs no database fallback");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection)) await RunSql(connection,sender,repo,transport,store,check);
    }

    private static async Task RunSql(string connection,ITelegramMessageSender sender,ISalesListRepository repo,
        PurchasePhotoSender transport,PurchasePhotoRepository store,Action<bool,string> check)
    {
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev" || selector.DataSource!="127.0.0.1,14333")
            throw new InvalidOperationException("Photo integration tests require the isolated development database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var perfumeId=await db.Perfumes.Select(value=>value.Id).FirstAsync();
        var variant="photo-fixture-"+Guid.NewGuid().ToString("N");var start=Random.Shared.Next(500000,900000);
        SalesList Fixture(int offset,string name,string url,string? photo) => new() {
            Id=Guid.NewGuid(),PublicCode=start+offset,PerfumeId=perfumeId,EnglishName=name,PersianName="Fixture",
            ProductPageUrl=url,DisplayBrand="Fixture",TelegramPhotoFileId=photo,TotalVolume=100,
            Status=SalesListStatus.Open,Gender=PerfumeGender.Unisex,CreatedAt=DateTime.UtcNow,
            OpenDate=DateTime.UtcNow.AddMinutes(offset) };
        var matching=Fixture(0,variant,"https://example.invalid/exact","older-exact-photo");
        var wrongName=Fixture(1,variant+" EDT","https://example.invalid/exact","wrong-concentration");
        var wrongUrl=Fixture(2,variant,"https://example.invalid/other","wrong-product");
        var ids=new[]{matching.Id,wrongName.Id,wrongUrl.Id};
        db.AddRange(matching,wrongName,wrongUrl);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        try
        {
            var current=List();current.PerfumeId=perfumeId;current.EnglishName=variant;current.DisplayBrand="Fixture";
            current.ProductPageUrl="https://example.invalid/exact";current.TelegramPhotoFileId=" ";
            check(await SalesListPhotoResolver.ResolveAsync(db,current,CancellationToken.None)=="older-exact-photo",
                "fallback SQL selects the exact variant instead of a newer wrong concentration or product URL");
            transport.Calls.Clear();store.Saves=0;
            await Alert(Controller(sender,repo,db),current);
            check(transport.Calls.Single().Photo=="older-exact-photo" && current.LowStockAlertSentAt.HasValue,
                "buying alert actually uses the recovered exact-variant image");
            current=List();current.TelegramPhotoFileId=null;current.PerfumeId=perfumeId;current.EnglishName=variant+" missing";
            transport.Calls.Clear();store.Saves=0;
            await Alert(Controller(sender,repo,db),current);
            check(transport.Calls.Count==0 && store.Saves==0 && current.LowStockAlertSentAt is null,
                "missing perfume photo defers the alert without sending a text-only fallback or marking delivery");
            current.TelegramPhotoFileId="newly-added-photo";
            await Alert(Controller(sender,repo,db),current);
            check(transport.Calls.Single().Photo=="newly-added-photo" && current.LowStockAlertSentAt.HasValue,
                "adding the missing photo allows the deferred purchase alert on the next refresh");
        }
        finally { await db.SalesLists.Where(value=>ids.Contains(value.Id)).ExecuteDeleteAsync(); }
    }
}

public class PurchasePhotoSender:DispatchProxy
{
    public bool Success=true;
    public List<(string Chat,string Photo,string Caption)> Calls { get; }=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name!="SendPhotoWithKeyboardAsync") throw new InvalidOperationException("Alert tried a non-photo send: "+method.Name);
        Calls.Add(((string)args![0]!, (string)args[1]!, (string)args[2]!));
        return Task.FromResult(new TelegramSendResult(Success,Success?null:"temporary photo failure"));
    }
}

public class PurchasePhotoRepository:DispatchProxy
{
    public int Saves;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="SaveChangesAsync") {Saves++;return Task.CompletedTask;}
        if(method.Name=="UpdateAsync") return Task.CompletedTask;
        throw new NotSupportedException(method.Name);
    }
}
