using System.Reflection;
using System.Runtime.CompilerServices;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class BottleUpgradeTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const long owner=771234567,admin=771234568;const string prefix="invoiceadmin:bup:";
        check((prefix+"target:"+Guid.NewGuid().ToString("N")).Length<=64,"bottle-upgrade buttons fit Telegram's callback-data limit");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="bup_"+Guid.NewGuid().ToString("N")[..18];var service=new SmallBottleUpgradeService(db);
        var bottles=new[]{1,2,3,5,10,15,20}.Select(v=>new Bottle{Id=Guid.NewGuid(),Name=marker+v,VolumeMl=v,Type=v<=3||v==20?BottleType.Normal:BottleType.Fancy,SalePrice=v*1000,CreatedAt=DateTime.UtcNow,IsActive=true}).ToArray();
        var list=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(8000000,9000000),PersianName=marker,EnglishName=marker,PricePerMl=10000,TotalVolume=5,ReservedVolume=5,Status=SalesListStatus.Full,CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow,PerfumeId=await db.Perfumes.Select(v=>v.Id).FirstAsync()};
        SalesListRequest Request(decimal volume,bool gift=false)=>new(){Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=gift?"991122335":"991122334",TelegramUsername=gift?marker+"giver":marker+"buyer",IsGift=gift,GiftRecipientTelegramUsername=gift?marker+"buyer":null,
            GiftRecipientTelegramUserId=gift?"991122334":null,VolumeMl=volume,BottleId=bottles.Single(v=>v.VolumeMl==volume).Id,BottlePrice=volume*1000,PerfumePricePerMl=10000,Status=SalesListRequestStatus.Confirmed,ConfirmedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddDays(1),ExternalReference=marker+Guid.NewGuid().ToString("N")};
        var own=Request(3);var gift=Request(2,true);db.Bottles.AddRange(bottles);db.SalesLists.Add(list);db.SalesListRequests.AddRange(own,gift);await db.SaveChangesAsync();
        var sender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var sent=(MarketingSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var f in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))if(f.Name.EndsWith("Drafts")&&f.FieldType.GetConstructor(Type.EmptyTypes)is not null)f.SetValue(controller,Activator.CreateInstance(f.FieldType));
        foreach(var (name,value) in new(string,object)[]{("_sender",sender),("_db",db),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),AdminChatId="-100123"}),("_adminActivityContext",new AdminActivityContext())})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        Task Select(string action,long user=owner,string chatType="private")=>(Task)typeof(TelegramWebhookController).GetMethod("HandleBottleUpgradeCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramCallbackQuery("fixture",new(user,null),new TelegramMessage(null,new(user,chatType),new(user,null),null,MessageId:1),prefix+action),CancellationToken.None])!;
        Task<bool> Input(string text,long user=owner)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleBottleUpgradeMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramMessage(text,new(user,"private"),new(user,null),null,MessageId:2),CancellationToken.None])!;
        async Task<bool> Reject(Guid request,Guid target,decimal price,byte[]? version=null)
        {try{await service.UpgradeAsync(request,version??(await service.PreviewAsync(request)).RowVersion,target,price,owner);return false;}catch(InvalidOperationException){db.ChangeTracker.Clear();return true;}}
        try
        {
            await Select("start",owner+9);check(sent.Texts.Last().Contains("فقط برای ادمین"),"customer cannot open or invoke bottle upgrade");
            sent.AllowAccountingAdmin=true;await Select("start",admin,"supergroup");check(sent.Texts.Last().Contains("گفت‌وگوی خصوصی"),"bottle upgrade rejects group callbacks even from authorized admins");
            await Select("start",admin);await Input(list.PublicCode.ToString(),admin);
            check(sent.Buttons.Contains(prefix+"list:"+list.Id.ToString("N")),"accounting admin can search a completed but uninvoiced list for bottle upgrade");
            await Select("list:"+list.Id.ToString("N"),admin);await Input("@"+marker+"buyer",admin);
            check(new[]{own,gift}.All(r=>sent.Buttons.Contains(prefix+"item:"+r.Id.ToString("N"))),"customer search includes own purchase and gifts received without combining their perfume volumes");
            await Select("item:"+own.Id.ToString("N"),admin);sent.Buttons.Clear();
            // Reopen once to capture only currently offered bottle choices.
            await Select("cancel",admin);await Select("start",admin);await Input(list.PublicCode.ToString(),admin);await Select("list:"+list.Id.ToString("N"),admin);await Input("@"+marker+"buyer",admin);sent.Buttons.Clear();await Select("item:"+own.Id.ToString("N"),admin);
            check(bottles.Where(b=>b.VolumeMl>=5).All(b=>sent.Buttons.Contains(prefix+"target:"+b.Id.ToString("N")))&&bottles.Where(b=>b.VolumeMl<=3).All(b=>!sent.Buttons.Contains(prefix+"target:"+b.Id.ToString("N")))&&sent.Buttons.All(v=>v.Length<=64),"all fitting normal and fancy bottles appear; undersized and current bottles are omitted");
            await Select("target:"+bottles.Single(v=>v.VolumeMl==5).Id.ToString("N"),admin);
            check(sent.Texts.Last().Contains("مقدار عطر: 3 میل")&&sent.Texts.Last().Contains("5,000")&&sent.Texts.Last().Contains("تأیید تغییر"),"preview shows unchanged perfume volume and new bottle price before saving");
            var save=sent.Buttons.Last(v=>v.StartsWith(prefix+"save:"));await Select(save[prefix.Length..],admin);await Select(save[prefix.Length..],admin);db.ChangeTracker.Clear();
            var updated=await db.SalesListRequests.AsNoTracking().SingleAsync(v=>v.Id==own.Id);var untouchedGift=await db.SalesListRequests.AsNoTracking().SingleAsync(v=>v.Id==gift.Id);
            check(updated.VolumeMl==3&&updated.BottleId==bottles.Single(v=>v.VolumeMl==5).Id&&updated.BottlePrice==5000&&updated.AdminNotes==own.AdminNotes&&untouchedGift.BottleId==gift.BottleId&&untouchedGift.VolumeMl==2&&untouchedGift.GiftRecipientTelegramUsername==gift.GiftRecipientTelegramUsername,
                "confirmed upgrade changes only selected bottle and preserves volume, notes and gift identities");
            check((await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==list.Id)).ReservedVolume==5&&await db.NotificationOutbox.CountAsync(v=>v.EventType=="AdminBottleChanged"&&v.Recipient==list.Id.ToString())==1,
                "replayed confirmation cannot change reserved stock or duplicate durable channel refresh");
            check(await Reject(gift.Id,bottles.Single(v=>v.VolumeMl==1).Id,1000),"undersized bottle cannot be forced through a forged callback");
            await db.Bottles.Where(v=>v.VolumeMl==15&&v.Name.StartsWith(marker)).ExecuteUpdateAsync(v=>v.SetProperty(b=>b.IsActive,false));
            check(await Reject(gift.Id,bottles.Single(v=>v.VolumeMl==15).Id,15000),"inactive bottle cannot be selected even with sufficient capacity");
            await db.Bottles.Where(v=>v.VolumeMl==15&&v.Name.StartsWith(marker)).ExecuteUpdateAsync(v=>v.SetProperty(b=>b.IsActive,true));
            check(await Reject(own.Id,bottles.Single(v=>v.VolumeMl==5).Id,5000),"selecting the existing bottle cannot create a duplicate change");
            var larger=await service.PreviewAsync(own.Id);
            await service.UpgradeAsync(own.Id,larger.RowVersion,bottles.Single(v=>v.VolumeMl==15).Id,15000,admin);db.ChangeTracker.Clear();
            var normal=await service.PreviewAsync(own.Id);
            await service.UpgradeAsync(own.Id,normal.RowVersion,bottles.Single(v=>v.VolumeMl==3).Id,3000,admin);db.ChangeTracker.Clear();
            check((await service.PreviewAsync(own.Id)).Bottle!.Type==BottleType.Normal,"admin can change a larger fancy bottle back to a fitting normal bottle");
            var restore=await service.PreviewAsync(own.Id);await service.UpgradeAsync(own.Id,restore.RowVersion,bottles.Single(v=>v.VolumeMl==5).Id,5000,admin);db.ChangeTracker.Clear();
            check(await Reject(gift.Id,bottles.Single(v=>v.VolumeMl==10).Id,9000),"catalog price change invalidates a stale bottle confirmation");
            var before=await service.PreviewAsync(gift.Id);await db.SalesListRequests.Where(v=>v.Id==gift.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.AdminNotes,"edited-after-preview"));
            check(await Reject(gift.Id,bottles.Single(v=>v.VolumeMl==10).Id,10000,before.RowVersion),"concurrent request edit invalidates the old confirmation version");
            var fresh=await service.PreviewAsync(gift.Id);await service.UpgradeAsync(gift.Id,fresh.RowVersion,bottles.Single(v=>v.VolumeMl==10).Id,10000,admin);db.ChangeTracker.Clear();
            check((await db.SalesListRequests.AsNoTracking().SingleAsync(v=>v.Id==gift.Id)).IsGift,"gift item itself can use fancy 10 ml without losing gift status");
            var largeRequest=Request(10);db.SalesListRequests.Add(largeRequest);
            await db.SalesLists.Where(v=>v.Id==list.Id).ExecuteUpdateAsync(v=>v.SetProperty(l=>l.TotalVolume,15m).SetProperty(l=>l.ReservedVolume,15m));
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var largePreview=await service.PreviewAsync(largeRequest.Id);await service.UpgradeAsync(largeRequest.Id,largePreview.RowVersion,bottles.Single(v=>v.VolumeMl==20).Id,20000,admin);db.ChangeTracker.Clear();
            check((await service.PreviewAsync(largeRequest.Id)).VolumeMl==10,"perfume requests above 3 ml can change bottle size without changing their perfume quantity");
            var billingSender=DispatchProxy.Create<ISender,ReadyBillingSender>();((ReadyBillingSender)(object)billingSender).Db=db;
            await new InvoiceIssuanceService(db,billingSender).IssueCompletedListsAsync([list.Id],owner.ToString());db.ChangeTracker.Clear();
            var items=await db.OrderItems.AsNoTracking().Where(v=>v.SalesListId==list.Id).ToArrayAsync();
            check(items.Count(v=>v.SourceSalesListRequestId==own.Id)==1&&items.Single(v=>v.SourceSalesListRequestId==own.Id).RequestedVolumeMl==3&&items.Single(v=>v.SourceSalesListRequestId==own.Id).BottlePrice==5000&&items.Single(v=>v.SourceSalesListRequestId==own.Id).LineTotal==35000,
                "invoice uses fancy 5 ml bottle price with original 3 ml perfume amount");
            check(items.Single(v=>v.SourceSalesListRequestId==gift.Id).RequestedVolumeMl==2&&items.Single(v=>v.SourceSalesListRequestId==gift.Id).BottlePrice==10000&&items.Single(v=>v.SourceSalesListRequestId==gift.Id).LineTotal==30000,
                "gift-giver invoice keeps 2 ml perfume and selected fancy 10 ml bottle charge");
            check(items.Single(v=>v.SourceSalesListRequestId==largeRequest.Id).RequestedVolumeMl==10&&items.Single(v=>v.SourceSalesListRequestId==largeRequest.Id).BottlePrice==20000&&items.Single(v=>v.SourceSalesListRequestId==largeRequest.Id).LineTotal==120000,
                "invoice bills 10 ml perfume plus the selected normal 20 ml bottle without billing 20 ml perfume");
            check(await Reject(own.Id,bottles.Single(v=>v.VolumeMl==10).Id,10000,updated.RowVersion),"invoiced items cannot be modified by an old bottle-upgrade callback");
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orders=await db.OrderItems.Where(v=>v.SalesListId==list.Id).Select(v=>v.OrderId).Distinct().ToArrayAsync();
            await db.NotificationOutbox.Where(v=>v.Recipient==list.Id.ToString()||(v.OrderId!=null&&orders.Contains(v.OrderId.Value))||v.Payload.Contains(marker)).ExecuteDeleteAsync();
            await db.OrderItems.Where(v=>v.SalesListId==list.Id).ExecuteDeleteAsync();
            await db.Invoices.Where(v=>orders.Contains(v.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(v=>orders.Contains(v.Id)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatchSalesLists.Where(v=>v.SalesListId==list.Id).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(v=>v.SalesListId==list.Id).ExecuteDeleteAsync();await db.SalesLists.Where(v=>v.Id==list.Id).ExecuteDeleteAsync();
            var bottleIds=bottles.Select(v=>v.Id).ToArray();await db.Bottles.Where(v=>bottleIds.Contains(v.Id)).ExecuteDeleteAsync();
        }
    }
}
