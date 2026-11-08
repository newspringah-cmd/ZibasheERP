using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class PurchaseCostTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var t=new DateTime(2026,11,1,10,0,0,DateTimeKind.Utc);
        var list=new SalesList{Id=Guid.NewGuid(),PerfumeId=Guid.NewGuid(),TotalVolume=100,ReservedVolume=100,ClosedDate=t,EnglishName="Cost fixture",Status=SalesListStatus.Invoiced};
        var price=new PerfumePurchasePrice{Id=Guid.NewGuid(),SalesListId=list.Id,VariantKey=PerfumePurchaseCostService.VariantKey(list),AmountAed=200,BottleVolumeMl=100,CreatedAt=t.AddDays(-1)};
        var rates=new[]{new DirhamExchangeRate{TomanPerDirham=25000,EffectiveAtUtc=t.AddDays(-2)},new DirhamExchangeRate{TomanPerDirham=30000,EffectiveAtUtc=t.AddHours(1)}};
        PerfumePurchaseCost Calculate(SalesList l,IEnumerable<PerfumePurchasePrice>? p=null,IEnumerable<DirhamExchangeRate>? r=null,DateTime? estimate=null)=>PerfumePurchaseCostService.Calculate(l,[],p??[price],r??rates,estimate);
        check(Calculate(list).CostToman==5000000&&Calculate(list).Rate==25000,"dirham purchase cost uses whole bottle AED price times rate at completion, not invoice/report date");
        check(Calculate(list,r:rates.Take(1)).CostToman==5000000,"a registered exchange rate remains valid without a daily replacement");
        check(Calculate(list,r:[new(){TomanPerDirham=1,EffectiveAtUtc=t}]).CostToman==200,"a rate effective exactly at completion is included");
        check(Calculate(list,r:rates.Skip(1)).CostToman is null&&Calculate(list,r:rates.Skip(1)).Missing.Contains("نرخ"),"future rates cannot backfill missing historical rates");
        check(Calculate(list,p:[]).CostToman is null&&Calculate(list,p:[]).Missing.Contains("قیمت"),"missing purchase price is unknown rather than zero cost");
        var next=new SalesList{Id=Guid.NewGuid(),PerfumeId=list.PerfumeId,EnglishName=list.EnglishName,TotalVolume=100,ReservedVolume=100,ClosedDate=t.AddDays(2)};
        check(Calculate(next).CostToman==6000000,"later cycles inherit the same variant and bottle-volume purchase price with their own completion rate");
        var future=new PerfumePurchasePrice{Id=Guid.NewGuid(),SalesListId=next.Id,VariantKey=price.VariantKey,AmountAed=300,BottleVolumeMl=100,CreatedAt=t.AddDays(1)};
        check(Calculate(list,[price,future]).CostToman==5000000,"a new price on a later cycle cannot rewrite an earlier cycle cost");
        var historical=new PerfumePurchasePrice{Id=Guid.NewGuid(),SalesListId=list.Id,VariantKey=price.VariantKey,AmountAed=210,BottleVolumeMl=100,CreatedAt=t.AddDays(10)};
        check(Calculate(list,[historical]).CostToman==5250000,"explicit historical price entry for a selected cycle still uses the historical completion rate");
        next.TotalVolume=50;check(Calculate(next).CostToman is null,"a different bottle volume cannot inherit the wrong full-bottle purchase price");
        next.TotalVolume=100;next.EnglishName="Different concentration";check(Calculate(next).CostToman is null,"different perfume variants cannot share purchase price accidentally");
        list.ClosedDate=null;check(Calculate(list).CostToman is null&&Calculate(list).Missing.Contains("زمان"),"missing completion time never falls back to today's exchange rate");
        check(Calculate(list,estimate:t).Estimate&&Calculate(list,estimate:t).CostToman==5000000,"open-list purchasing estimates are explicitly labelled and use the current rate");
        var requests=new[]{new SalesListRequest{Kind=SalesListRequestKind.CurrentBottle,Status=SalesListRequestStatus.Invoiced,ConfirmedAt=t}};
        check(PerfumePurchaseCostService.Calculate(list,requests,[price],rates).CostToman==5000000,"legacy completed lists resolve their actual last confirmed request timestamp");
        var parser=typeof(TelegramWebhookController).GetMethod("TryPurchaseAmount",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var input in new[]{"۲۵٬۰۰۰","٢٥٠٠٠","200.5","۲۰۰٫۵"})
        {object?[] args=[input,0m];check((bool)parser.Invoke(null,args)!,"purchase amount accepts Persian/Arabic digits or decimal input: "+input);}
        foreach(var input in new[]{"0","-1","NaN","1e4","1000000001","0.00001"})
        {object?[] args=[input,0m];check(!(bool)parser.Invoke(null,args)!,"purchase amount rejects invalid or unsafe input: "+input);}

        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new InvalidOperationException("Isolated SQL required");
        var selector=new SqlConnectionStringBuilder(connection);if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Development DB only");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=741471928,staff=1907823600;var sender=DispatchProxy.Create<ITelegramMessageSender,PurchaseCostSender>();var transport=(PurchaseCostSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var field in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))
            if(field.Name.EndsWith("Drafts")&&field.FieldType.GetConstructor(Type.EmptyTypes)!=null)field.SetValue(controller,Activator.CreateInstance(field.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString()}),("_adminActivityContext",new AdminActivityContext())})
            typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task<object?> Call(string name,params object?[] args){var task=(Task)typeof(TelegramWebhookController).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!.Invoke(controller,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}
        async Task Select(string action,long actor=owner,string type="private")=>await Call("TryHandlePurchaseCostCallbackAsync",new TelegramCallbackQuery("test-cost",new(actor,null),new TelegramMessage(null,new(actor,type),new(actor,null),null,MessageId:47),"purchase-cost:"+action),CancellationToken.None);
        async Task Input(string text,long actor=owner)=>await Call("TryHandlePurchaseCostMessageAsync",new TelegramMessage(text,new(actor,"private"),new(actor,null),null,MessageId:48),CancellationToken.None);
        var marker="درهم تست "+Guid.NewGuid().ToString("N");var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();
        var fixture=new SalesList{Id=Guid.NewGuid(),PerfumeId=perfume,PublicCode=Random.Shared.Next(8000000,9000000),PersianName=marker,EnglishName=marker,
            TotalVolume=100,ReservedVolume=95,Status=SalesListStatus.Open,TelegramPhotoFileId="cost-photo",PricePerMl=30000,CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow};
        var operations=new List<Guid>();
        async Task<Guid> Confirm()
        {var callback=transport.Buttons.Last(b=>b.StartsWith("purchase-cost:save:"));var token=Guid.ParseExact(callback["purchase-cost:save:".Length..],"N");operations.Add(token);await Select(callback["purchase-cost:".Length..]);return token;}
        try
        {
            db.SalesLists.Add(fixture);await db.SaveChangesAsync();
            await Call("SendInvoiceAdminMenuAsync",staff,null,CancellationToken.None,null);
            check(!transport.Buttons.Contains("purchase-cost:price")&&!transport.Buttons.Contains("purchase-cost:rate"),"both dirham menu controls are hidden from accountants");
            await Call("SendInvoiceAdminMenuAsync",owner,null,CancellationToken.None,null);
            check(transport.Buttons.Contains("purchase-cost:price")&&transport.Buttons.Contains("purchase-cost:rate"),"only numeric primary owner receives purchase price and rate menu controls");
            var before=await db.DirhamExchangeRates.CountAsync();await Select("rate",staff);await Input("25000",staff);await Select("rate",owner,"supergroup");
            check(await db.DirhamExchangeRates.CountAsync()==before&&!transport.Buttons.Any(b=>b.StartsWith("purchase-cost:save:")),"forged staff/group callbacks cannot start or persist a financial rate");
            await Select("rate");await Input("۲۵٬۰۰۰");check(await db.DirhamExchangeRates.CountAsync()==before,"rate entry shows confirmation without writing prematurely");
            var rateToken=await Confirm();await Select("save:"+rateToken.ToString("N"));
            check(await db.DirhamExchangeRates.CountAsync(r=>r.Id==rateToken)==1,"rate confirmation is durable and cannot duplicate on repeated clicks");
            await Select("price");await Input(marker);
            check(transport.Photos.Any(p=>p.Caption.Contains(marker)&&p.File=="cost-photo"),"Persian-name search displays the selected perfume photo and code");
            await Select("pick:"+fixture.Id.ToString("N"));await Input("۲۰۰");await Confirm();
            check(await db.PerfumePurchasePrices.AnyAsync(p=>p.SalesListId==fixture.Id&&p.AmountAed==200&&p.BottleVolumeMl==100),"owner-selected full bottle purchase price is saved independently of selling price");
            fixture.ClosedDate=DateTime.UtcNow;fixture.Status=SalesListStatus.Invoiced;fixture.ReservedVolume=100;await db.SaveChangesAsync();
            var service=new PerfumePurchaseCostService(db);var initial=(await service.CostsAsync([fixture],default)).Single();
            check(initial.CostToman==5000000&&initial.Rate==25000,"SQL-backed report resolves the rate valid when the list completed");
            await Select("rate");await Input("۳۰۰۰۰");await Confirm();
            check((await service.CostsAsync([fixture],default)).Single().CostToman==5000000,"a later saved daily rate cannot alter completed-list purchase reports");
            check((await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==fixture.Id)).PricePerMl==30000,"purchase entry leaves the customer's selling price unchanged");
            await Select("price");await Input(fixture.PublicCode.ToString());check(transport.Photos.Count>=2,"numeric list-code search also returns the perfume with its photo");
            await Select("pick:"+fixture.Id.ToString("N"));await Input("250");var stale=transport.Buttons.Last(b=>b.StartsWith("purchase-cost:save:"));await Select("cancel");await Select(stale["purchase-cost:".Length..]);
            check(await db.PerfumePurchasePrices.CountAsync(p=>p.SalesListId==fixture.Id)==1,"cancelled purchase-price confirmation cannot append a correction");
            var direct=Guid.NewGuid();operations.Add(direct);await service.RegisterRateAsync(direct,31000,owner,default);await service.RegisterRateAsync(direct,31000,owner,default);
            check(await db.DirhamExchangeRates.CountAsync(r=>r.Id==direct)==1,"storage layer makes repeated rate confirmations idempotent independently of draft state");
            var refused=false;try{await service.RegisterRateAsync(direct,32000,owner,default);}catch(InvalidOperationException){refused=true;}
            check(refused,"an operation identifier cannot be reused for a different exchange rate");
        }
        finally
        {
            await Select("cancel");await db.PerfumePurchasePrices.Where(p=>p.SalesListId==fixture.Id).ExecuteDeleteAsync();
            await db.DirhamExchangeRates.Where(r=>operations.Contains(r.Id)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>l.Id==fixture.Id).ExecuteDeleteAsync();
        }
    }
}

public class PurchaseCostSender:DispatchProxy
{
    public List<string> Buttons=new();public List<(string Chat,string File,string Caption)> Photos=new();
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        foreach(var value in args??[])if(value is IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows)Buttons.AddRange(rows.SelectMany(r=>r).Select(b=>b.CallbackData??""));
        if(method!.Name=="SendPhotoWithKeyboardAsync")Photos.Add(((string)args![0]!, (string)args[1]!, (string)args[2]!));
        return Task.FromResult(new TelegramSendResult(true,MessageId:777));
    }
}
