using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class HourlyPromotionTests
{
    const long Owner=771234567;
    public static async Task Run(Action<bool,string> check)
    {
        var offer=new FlashSalePromotion {OriginalPricePerMl=20000,DiscountPercent=0,DiscountAmountPerMl=3500};
        check(offer.DiscountedPricePerMl==16500,"fixed per-ml discount subtracts the approved amount");
        var timed=new FlashSalePromotion{StartsAtUtc=new DateTime(2026,11,11,20,20,0),EndsAtUtc=new DateTime(2026,11,12,1,20,0),RequestedDurationMinutes=300,OriginalPricePerMl=764000,DiscountAmountPerMl=200000};
        var named=new SalesList{PublicCode=5867,EnglishName="Synthetic extension",TotalVolume=100};var clock=new DateTime(2026,11,11,22,0,0);
        check(HourlyPromotionCopy.Caption(timed,named,clock).Contains("۵ ساعت فرصت"),"unextended promotions retain their original advertised duration");
        timed.EndsAtUtc=new DateTime(2026,11,12,8,29,0);
        var extended=HourlyPromotionCopy.Caption(timed,named,clock);
        check(extended.Contains("ساعت ۱۱:۵۹ روز ۱۴۰۵/۰۸/۲۱")&&extended.Contains("زمان باقی‌مانده: ۱۰:۲۹")&&!extended.Contains("۵ ساعت فرصت"),
            "an extended promotion displays its exact Tehran deadline and Persian date instead of the obsolete original duration");
        check(timed.RequestedDurationMinutes==300&&timed.DiscountedPricePerMl==564000&&!HourlyPromotionCopy.Caption(timed,named,timed.EndsAtUtc).Contains("زمان باقی‌مانده"),
            "deadline display preserves original campaign settings and ends at the same exclusive pricing boundary");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if(string.IsNullOrWhiteSpace(connection))return;
        var target=new SqlConnectionStringBuilder(connection);
        if(target.InitialCatalog!="ZibasheERPCloudDev" || target.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var list=new SalesList {Id=Guid.NewGuid(),PerfumeId=await db.Perfumes.Select(v=>v.Id).FirstAsync(),PublicCode=Random.Shared.Next(4000000,5000000),
            EnglishName="Hourly <fixture>",PersianName="آزمایش",PricePerMl=20000,TotalVolume=100,Status=SalesListStatus.Open,
            OpenDate=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,TelegramPhotoFileId="fixture-not-real",TelegramChannelId="-1001236168661",TelegramMessageId=7};
        db.SalesLists.Add(list);await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var sent=(MarketingSender)(object)sender;
        var c=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var f in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(f.Name.EndsWith("Drafts",StringComparison.Ordinal)&&f.FieldType.GetConstructor(Type.EmptyTypes)is not null)f.SetValue(c,Activator.CreateInstance(f.FieldType));
        foreach(var (name,value) in new (string,object)[]{("_sender",sender),("_db",db),("_options",new TelegramOptions{OwnerUserId=Owner.ToString()}),("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(c,value);
        Task Select(string action)=>(Task)typeof(TelegramWebhookController).GetMethod("HandleFlashCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,
            [new TelegramCallbackQuery("fixture",new(Owner,null),new TelegramMessage(null,new(Owner,"private"),new(Owner,null),null,MessageId:1),"invoiceadmin:flash:"+action),CancellationToken.None])!;
        Task<bool> Input(string text)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleFlashMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,
            [new TelegramMessage(text,new(Owner,"private"),new(Owner,null),null,MessageId:2),CancellationToken.None])!;
        var campaign=Guid.NewGuid();
        try
        {
            await Select("new:list");await Input(list.DisplayCode.ToString());await Input("۰٫۵");await Select("mode:amount");await Input("۳۵۰۰");
            check(sent.Texts.Last().Contains("۱۶٬۵۰۰")&&sent.Texts.Last().Contains("&lt;fixture&gt;"),"hourly preview shows fixed discounted price and escapes perfume name");
            var confirm=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:flash:start:"));
            check(!await db.FlashSalePromotions.AnyAsync(v=>v.SalesListId==list.Id)&&!await db.NotificationOutbox.AnyAsync(v=>v.Payload.Contains(list.Id.ToString())),
                "hourly photo preview does not activate pricing or queue public posts");
            await Select(confirm["invoiceadmin:flash:".Length..]);await Select(confirm["invoiceadmin:flash:".Length..]);
            var jobs=await db.NotificationOutbox.Where(v=>v.Payload.Contains(list.Id.ToString())).ToArrayAsync();
            var payload=JsonSerializer.Deserialize<MarketingPromotionJob>(jobs.Single().Payload)!;
            check(payload.FlashSale is {DurationMinutes:30,DiscountPercent:0,DiscountAmountPerMl:3500,OriginalPricePerMl:20000}&&payload.AdminUserId==Owner,
                "approved Persian half-hour fixed discount queues exactly once for the verified manager");
            var api=new FlashSalesController(db,Options.Create(new TelegramOptions{OwnerUserId=Owner.ToString()}));
            var request=new FlashSalesController.CreateRequest(campaign,list.Id,0,30,3500,Owner,20000);
            check(await api.Create(request with{ActorUserId=Owner+1},default)is ForbidResult,"hourly activation rejects nonmanager actor");
            check(await api.Create(request with{DiscountAmountPerMl=20000},default)is BadRequestObjectResult,"hourly discount cannot make perfume free or negative");
            check(await api.Create(request with{ExpectedOriginalPricePerMl=10000},default)is ConflictObjectResult,"changed perfume price requires a fresh preview");
            check(await api.Create(request,default)is OkObjectResult&&await api.Create(request,default)is OkObjectResult,"fixed hourly activation is durable and idempotent");
            var quote=await FlashSalePricing.QuoteAsync(db,list,DateTime.UtcNow);
            check(quote.PricePerMl==16500&&quote.PromotionId==campaign,"customer and admin common price quote uses fixed campaign amount");
            await Select("end-confirm:"+campaign.ToString("N"));
            db.ChangeTracker.Clear();
            var ended=await db.FlashSalePromotions.SingleAsync(v=>v.Id==campaign);
            check(!ended.IsActiveAt(DateTime.UtcNow)&&(await FlashSalePricing.QuoteAsync(db,list,DateTime.UtcNow)).PricePerMl==20000,
                "early-end bot confirmation immediately stops pricing before announcement worker runs");
            var endJob=await db.NotificationOutbox.SingleAsync(v=>v.EventType=="HourlyPromotionEnd"&&v.Payload.Contains(campaign.ToString()));
            check(endJob.Status==NotificationOutboxStatus.Pending,"early end durably queues both-destination thank-you announcement");
            check(await api.Create(request,default)is OkObjectResult&&(await db.FlashSalePromotions.AsNoTracking().SingleAsync(v=>v.Id==campaign)).EndsAtUtc==ended.EndsAtUtc,
                "retrying approved activation cannot reactivate an ended campaign");
            await Select("new:list");await Input(list.DisplayCode.ToString());await Input("۲");await Select("mode:percent");await Input("۲۵");
            check(sent.Texts.Last().Contains("۱۵٬۰۰۰")&&sent.Texts.Last().Contains("۲۵٪"),"percent wizard previews the requested discount independently of fixed amount mode");
            await Select("menu");
        }
        finally
        {
            await db.NotificationOutbox.Where(v=>v.Payload.Contains(list.Id.ToString())||v.Payload.Contains(campaign.ToString())).ExecuteDeleteAsync();
            await db.FlashSalePromotions.Where(v=>v.SalesListId==list.Id).ExecuteDeleteAsync();
            await db.SalesLists.Where(v=>v.Id==list.Id).ExecuteDeleteAsync();
        }
    }
}
