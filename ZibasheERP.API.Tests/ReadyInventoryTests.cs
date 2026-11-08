using System.Reflection;
using System.Runtime.CompilerServices;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Inventory;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Invoices;
using ZibasheERP.Application.Features.Invoices.IssueInvoice;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class ReadyInventoryTests
{
    const long Owner=771234567;
    public static async Task Run(Action<bool,string> check)
    {
        using(var oneShot=new TelegramMessageSender(Options.Create(new TelegramOptions{BotToken="fixture-token"})))
        {
            var field=typeof(TelegramMessageSender).GetField("_httpClient",BindingFlags.Instance|BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(oneShot)!).Dispose();var transport=new GroupReplyHttp{Status=System.Net.HttpStatusCode.TooManyRequests};
            field.SetValue(oneShot,new HttpClient(transport){BaseAddress=new Uri("https://example.invalid/")});
            var limited=await oneShot.DeleteMessageOnceAsync("-100222333",8);
            check(transport.Calls==1&&limited.IsRetryable&&limited.RetryAfterSeconds==120,"inventory deletion uses one HTTP attempt and passes the exact Telegram retry_after to its durable worker");
        }
        var stock=new SalesList{IsInventoryOffer=true,InventoryCode=1234,PublicCode=998877,Status=SalesListStatus.Open,TotalVolume=3,MinimumRequestVolumeMl=3,StoragePosition="PRIVATE-POSITION"};
        check(stock.DisplayCode==1234&&!stock.CanReserveVolume(1,true)&&stock.CanReserveVolume(3),"ready package uses independent code and cannot be split even by admin");
        check(!ReadyInventoryPresentation.Caption(stock).Contains("PRIVATE-POSITION"),"public ready inventory caption never exposes physical position");
        var shippingLine=(string)typeof(TelegramWebhookController).GetMethod("FormatShippingItemLine",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[new OrderItem{SalesList=stock,RequestedVolumeMl=3},0])!;
        check(shippingLine.Contains("PRIVATE-POSITION")&&shippingLine.Contains("1234"),"internal shipping report carries physical position and inventory package code");

        check((string)typeof(TelegramWebhookController).GetMethod("InventoryReferenceName",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,["نامشخص","دیور ساواج","Sauvage",1234])! == "دیور ساواج",
            "inventory reference label prefers a real Persian catalogue name over placeholder text");
        string InventoryCaption(string text,bool inventory)=>(string)typeof(N8nIntegrationsController).GetMethod("EnsureInventoryCaption",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[text,inventory])!;
        check(InventoryCaption("🧾 old invoice",true).StartsWith("🔴🔴موجودی🔴🔴\n")&&InventoryCaption("ordinary",false)=="ordinary", "final invoice send restores inventory heading for historical retry payloads without marking ordinary invoices");
        var tagged=InventoryCaption("🧾 old invoice",true);
        check(InventoryCaption(tagged,true)==tagged&&InventoryCaption(new string('x',1024),true).Length<=1024,"inventory retry heading is idempotent and respects Telegram caption limit");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var service=new ReadyInventoryService(db);var marker="readyfix_"+Guid.NewGuid().ToString("N")[..20];
        var bottle=new Bottle{Id=Guid.NewGuid(),Name=marker,VolumeMl=3,SalePrice=2000,IsActive=true,CreatedAt=DateTime.UtcNow};db.Bottles.Add(bottle);
        var reference=new SalesList{Id=Guid.NewGuid(),PerfumeId=await db.Perfumes.Select(v=>v.Id).FirstAsync(),PublicCode=Random.Shared.Next(6000000,7000000),EnglishName=marker,PersianName="عطر مرجع",TotalVolume=100,PricePerMl=10000,TelegramMessageId=Random.Shared.Next(30000000,40000000),TelegramChannelId="-1001236168661",TelegramPhotoFileId="fixture-photo",CreatedAt=DateTime.UtcNow,OpenDate=DateTime.UtcNow};db.SalesLists.Add(reference);await db.SaveChangesAsync();
        var ids=new List<Guid>{reference.Id};var perfumes=new List<Guid>();
        var originalBottleExisted=await db.Bottles.AnyAsync(v=>v.Id==ReadyInventoryService.OriginalBottleId);
        var sender=DispatchProxy.Create<ITelegramMessageSender,MarketingSender>();var sent=(MarketingSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var f in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))if(f.Name.EndsWith("Drafts")&&f.FieldType.GetConstructor(Type.EmptyTypes)is not null)f.SetValue(controller,Activator.CreateInstance(f.FieldType));
        foreach(var (name,value) in new (string,object)[]{("_sender",sender),("_db",db),("_options",new TelegramOptions{OwnerUserId=Owner.ToString(),ReadyInventoryChannelId="-100222333",InvoiceFailureChatId="-100444555"}),("_adminActivityContext",new AdminActivityContext())})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        Task Select(string action,long user=Owner)=>(Task)typeof(TelegramWebhookController).GetMethod("HandleReadyInventoryCallbackAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramCallbackQuery("fixture",new(user,null),new TelegramMessage(null,new(user,"private"),new(user,null),null,MessageId:1),"invoiceadmin:ready:"+action),CancellationToken.None])!;
        Task<bool> Input(string? text,bool photo=false,long user=Owner)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleReadyInventoryMessageAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new TelegramMessage(text,new(user,"private"),new(user,null),null,Photo:photo?[new("fixture-photo",100,100)]:null,MessageId:2),CancellationToken.None])!;
        async Task<SalesListRequest> Request(SalesList list,string user)
        {
            var request=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=marker+user,TelegramUsername=marker+user,VolumeMl=list.TotalVolume,BottleId=bottle.Id,BottlePrice=2000,
                PerfumePricePerMl=list.PricePerMl,CreatedAt=DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddMinutes(10),ExternalReference=marker+Guid.NewGuid().ToString("N")};
            db.SalesListRequests.Add(request);await db.SaveChangesAsync();return request;
        }
        try
        {
            await Select("menu",Owner+1);check(sent.Texts.Last().Contains("فقط برای ادمین"),"customer cannot open inventory admin or physical-location menu");
            await Select("sold-position",Owner+1);check(sent.Texts.Last().Contains("فقط برای ادمین"),"customer cannot start sold-inventory position registration");
            var listCount=await db.SalesLists.CountAsync();var jobCount=await db.NotificationOutbox.CountAsync();
            await Select("menu");check(sent.ButtonLabels.Contains("📦 ثبت موجودی فروش‌رفته"),"inventory admin menu includes sold-inventory location registration");
            await Select("sold-position");await Input("عطر مرجع");
            check(sent.Buttons.Contains("invoiceadmin:ready:pick-sold:"+reference.Id.ToString("N")),"sold-inventory location search accepts Persian perfume name");
            await Input(reference.PublicCode.ToString());await Select("pick-sold:"+reference.Id.ToString("N"));
            check(sent.Texts.Last().Contains("جایگاه فیزیکی"),"sold-inventory flow asks only for position after selecting the perfume code");
            await Input(new string('x',151));check(sent.Texts.Last().Contains("۱۵۰"),"invalid sold-inventory position is rejected without saving");
            await Input("SOLD-SHELF-7");db.ChangeTracker.Clear();
            var located=await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==reference.Id);
            check(located.StoragePosition=="SOLD-SHELF-7"&&!located.IsInventoryOffer&&located.Status==SalesListStatus.Open&&located.PricePerMl==10000&&located.TotalVolume==100&&located.ReservedVolume==0,
                "sold-inventory registration stores physical position without changing sale status, perfume amount, capacity or pricing");
            check(await db.SalesLists.CountAsync()==listCount&&await db.NotificationOutbox.CountAsync()==jobCount,
                "sold-inventory registration creates neither a sale offer nor any channel publication or invoice job");
            var soldShippingLine=(string)typeof(TelegramWebhookController).GetMethod("FormatShippingItemLine",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[new OrderItem{SalesList=located,RequestedVolumeMl=3},0])!;
            check(soldShippingLine.Contains("SOLD-SHELF-7")&&soldShippingLine.Contains(reference.PublicCode.ToString()),"post report shows registered position for an ordinary previously sold perfume list");
            await service.SetSoldPositionAsync(reference.Id,"SOLD-SHELF-7",Owner);
            check(await db.InventoryPositionChanges.CountAsync(v=>v.SalesListId==reference.Id)==1,"repeating the same sold-inventory position does not duplicate its audit entry");
            await Select("position");await Input(reference.PublicCode.ToString());
            check(sent.Texts.Any(v=>v.Contains("SOLD-SHELF-7"))&&sent.Buttons.Contains("invoiceadmin:ready:move-sold:"+reference.Id.ToString("N")),"existing position search also finds sold-inventory registration and offers internal position editing");
            await Select("move-sold:"+reference.Id.ToString("N"));await Input("SOLD-SHELF-8");
            check((await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==reference.Id)).StoragePosition=="SOLD-SHELF-8"&&await db.InventoryPositionChanges.CountAsync(v=>v.SalesListId==reference.Id)==2,
                "sold-inventory position can be corrected with old and new positions retained in admin audit");
            await Select("new");await Input(reference.PublicCode.ToString());check(sent.Buttons.Any(v=>v=="invoiceadmin:ready:pick:"+reference.Id.ToString("N")),"numeric code search finds channel list reference");
            check(sent.ButtonLabels.Contains($"عطر مرجع — {reference.PublicCode}"),"inventory search buttons show Persian perfume name and code without English clutter");
            await Input(marker[0..20]);check(sent.Buttons.Any(v=>v=="invoiceadmin:ready:pick:"+reference.Id.ToString("N")),"partial perfume name search finds channel references");
            await Select("pick:"+reference.Id.ToString("N"));
            check(sent.Photos.Last()=="fixture-photo"&&sent.SourcePhotos.Count==0&&sent.ButtonLabels.Contains("🖼 تغییر عکس"),
                "selecting a reference automatically previews its stored photo with an optional change-photo button");
            await Input("۳");await Select("bottle:"+bottle.Id.ToString("N"));await Select("default-bottle-price");await Input("PRIVATE-POSITION");
            check(sent.Texts.Last().Contains("توضیحات اختیاری")&&sent.Buttons.Contains("invoiceadmin:ready:skip-description"),"inventory creation asks for optional public description after physical position");
            await Input(new string('x',401));check(sent.Texts.Last().Contains("۴۰۰"),"oversized public description is rejected before approval");
            await Input("توضیح اولیه");var outdatedNoteSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:save:"));
            await Select("edit:description");await Input("شیشه خط دارد <b>تست</b> & توضیح");
            await Select(outdatedNoteSave["invoiceadmin:ready:".Length..]);
            check(!await db.SalesLists.AnyAsync(v=>v.InventoryReferenceListId==reference.Id),"editing public description invalidates the old approval button");
            // A stale-button warning does not replace the current photo preview.
            await Select("edit:description");await Input("شیشه خط دارد <b>تست</b> & توضیح");
            check(sent.Texts.Last().Contains("32,000")&&!sent.Texts.Last().Contains("PRIVATE-POSITION"),"inventory photo preview uses imported price and excludes location from public caption");
            check(sent.Texts.Last().Contains("🔴 <b>توضیحات:")&&sent.Texts.Last().Contains("&lt;b&gt;تست&lt;/b&gt; &amp;")&&sent.ButtonLabels.Contains("📝 اصلاح توضیحات"),"public inventory description is safely escaped in customer photo preview and can be edited");
            var save=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:save:"));
            check(!await db.SalesLists.AnyAsync(v=>v.InventoryCreatedByUserId==Owner&&v.InventoryReferenceListId==reference.Id),"inventory preview creates no stock or public job before approval");
            await Select(save["invoiceadmin:ready:".Length..]);await Select(save["invoiceadmin:ready:".Length..]);
            var ready=await db.SalesLists.SingleAsync(v=>v.InventoryReferenceListId==reference.Id);ids.Add(ready.Id);
            check(ready.InventoryCode is >=1000 and <=9999&&ready.PricePerMl==10000&&ready.TotalVolume==3&&reference.ReservedVolume==0,"approved imported inventory has independent four-digit code without touching reference capacity");
            check(await db.NotificationOutbox.CountAsync(v=>v.Id==ready.Id)==1,"replayed inventory publication confirmation queues only one durable job");
            check(ready.InventoryPublicDescription=="شیشه خط دارد <b>تست</b> & توضیح"&&ReadyInventoryPresentation.Caption(ready).Contains("توضیحات:"),"public description persists on inventory and remains present in later channel refreshes");
            check(!ReadyInventoryPresentation.Caption(new SalesList{Notes="PRIVATE-INTERNAL-NOTE",StoragePosition="PRIVATE-SHELF"}).Contains("PRIVATE"),"legacy internal notes and physical storage position are never exposed as public inventory description");
            await Select("manual");await Input(marker+"UI");await Input(null,true);await Input("۱۲۰۰۰");await Input("۱٫۵");
            await Select("bottle:"+bottle.Id.ToString("N"));await Select("default-bottle-price");await Input("manual-position");
            await Select("skip-description");
            check(sent.Texts.Last().Contains("20,000")&&!sent.Texts.Last().Contains("manual-position"),"manual inventory wizard accepts photo and Persian prices before a private approval preview");
            check(!sent.Texts.Last().Contains("توضیحات:"),"skipping optional description produces a clean caption with no empty description heading");
            var cancelledSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:save:"));await Select("menu");await Select(cancelledSave["invoiceadmin:ready:".Length..]);
            check(!await db.SalesLists.AnyAsync(v=>v.EnglishName==marker+"UI"),"cancelled inventory preview cannot later publish through an old save button");
            db.ChangeTracker.Clear();
            await db.SalesLists.Where(v=>v.Id==reference.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.TelegramPhotoFileId,(string?)null));
            var imported=new TelegramSalesListImport{Id=Guid.NewGuid(),SalesListId=reference.Id,SourceChannelId=reference.TelegramChannelId!,SourceMessageId=reference.TelegramMessageId!.Value,
                SourceDate=DateTimeOffset.UtcNow,TelegramPhotoFileId="imported-source-photo",CreatedAt=DateTime.UtcNow};db.TelegramSalesListImports.Add(imported);await db.SaveChangesAsync();
            await Select("new");await Input(reference.PublicCode.ToString());await Select("pick:"+reference.Id.ToString("N"));
            check(sent.Photos.Last()=="imported-source-photo"&&sent.SourcePhotos.Count==0,"reference recovers photo from its linked channel import instead of asking admin to upload");
            await db.TelegramSalesListImports.Where(v=>v.Id==imported.Id).ExecuteDeleteAsync();
            await Select("new");await Input(reference.PublicCode.ToString());await Select("pick:"+reference.Id.ToString("N"));
            check(sent.Photos.Last()=="retrieved-source-photo"&&sent.SourcePhotos.Last()==(reference.TelegramChannelId!,reference.TelegramMessageId.Value),
                "missing stored photo is recovered from the exact selected channel post for private preview");
            await Select("reference-photo");await Input(null,true);await Input("۳");
            check(sent.Texts.Last().Contains("شیشهٔ همین بسته"),"optional photo replacement resumes quantity entry without asking for the photo again");
            await Select("menu");
            await db.SalesLists.Where(v=>v.Id==reference.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.TelegramPhotoFileId,"fixture-photo"));
            var manualId=Guid.NewGuid();ids.Add(manualId);
            var manual=await service.CreateAsync(new(manualId,null,marker+"manual","fixture-photo",15000,3,bottle.Id,2000,"shelf-2",Owner));perfumes.Add(manual.PerfumeId);
            var manualAgain=await service.CreateAsync(new(manualId,null,marker+"manual","fixture-photo",15000,3,bottle.Id,2000,"shelf-2",Owner));
            check(manual.Id==manualAgain.Id&&manual.InventoryCode!=ready.InventoryCode,"manual inventory creation is idempotent and allocates a distinct package code");
            await service.SetPositionAsync(ready.Id,"shelf-3",Owner);
            ready=await db.SalesLists.SingleAsync(v=>v.Id==ready.Id);
            check(await db.InventoryPositionChanges.CountAsync(v=>v.SalesListId==ready.Id)==2&&ready.StoragePosition=="shelf-3"&&ready.PricePerMl==10000,"physical relocation keeps audit history without changing package price");
            var locationJobCount=await db.NotificationOutbox.CountAsync();
            await Select("menu");check(sent.ButtonLabels.Contains("📍 ثبت جایگاه موجودی"),"inventory menu exposes a separate physical-location registration option");
            await Select("register-position",Owner+1);check(sent.Texts.Last().Contains("فقط برای ادمین"),"physical-location entry rejects customers before opening a draft");
            await db.SalesLists.Where(l=>l.Id==manual.Id).ExecuteUpdateAsync(s=>s.SetProperty(l=>l.StoragePosition,(string?)null).SetProperty(l=>l.PersianName,"موجودی جایگاه تست"));
            await Select("register-position");var locationPhotosBeforeSearch=sent.Photos.Count;await Input("جایگاه تست");
            check(sent.Buttons.Contains("invoiceadmin:ready:locpick:"+manual.Id.ToString("N")),"location search finds ready inventory by a partial Persian perfume name");
            check(sent.Photos.Count==locationPhotosBeforeSearch+1&&sent.Texts.Last().Contains("کد موجودی: "+manual.InventoryCode!.Value)&&sent.Texts.Last().Contains("موجودی جایگاه تست"),"location name-search result shows its perfume photo, name and exact inventory code before the selection button is clicked");
            await Select("locpick:"+reference.Id.ToString("N"));check(sent.Texts.Last().Contains("آخرین جست‌وجو"),"location selection cannot substitute an ordinary list or an item outside the search results");
            var locationPhotosBeforeCode=sent.Photos.Count;await Input(string.Concat(manual.InventoryCode!.Value.ToString().Select(c=>(char)('۰'+c-'0'))));
            check(sent.Photos.Count==locationPhotosBeforeCode+1&&sent.ButtonLabels.Last(v=>v.StartsWith("انتخاب ")).Contains(manual.InventoryCode.Value.ToString()),"Persian-digit inventory-code search also returns a photo card bound to the same package");
            await Select("locpick:"+manual.Id.ToString("N"));
            check(sent.Texts.Last().Contains("مشخصات موجودی")&&sent.Texts.Last().Contains(manual.InventoryCode.Value.ToString())&&sent.Texts.Last().Contains("ثبت نشده"),"inventory-code selection previews the correct package before asking for a physical location");
            var locationInfoToken=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locok:"))["invoiceadmin:ready:".Length..];
            await Select(locationInfoToken);check(sent.Texts.Last().Contains("کدام جایگاه فیزیکی"),"an inventory package with no location proceeds to location entry only after information confirmation");
            await Input("LOC-A");var locationSaveToken=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locsave:"))["invoiceadmin:ready:".Length..];
            check(await db.SalesLists.AsNoTracking().AnyAsync(l=>l.Id==manual.Id&&l.StoragePosition==null),"typing a proposed location does not save before final confirmation");
            await Select(locationSaveToken);var locationAudit=await db.InventoryPositionChanges.CountAsync(v=>v.SalesListId==manual.Id);
            await Select(locationSaveToken);
            check(await db.SalesLists.AsNoTracking().AnyAsync(l=>l.Id==manual.Id&&l.StoragePosition=="LOC-A")&&await db.InventoryPositionChanges.CountAsync(v=>v.SalesListId==manual.Id)==locationAudit,"confirmed first location is stored once and repeated confirmation cannot duplicate its audit");
            await Select("register-position");await Input(manual.InventoryCode.Value.ToString());await Select("locpick:"+manual.Id.ToString("N"));
            await Select(sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locok:"))["invoiceadmin:ready:".Length..]);
            check(sent.Texts.Last().Contains("LOC-A")&&sent.Texts.Last().Contains("منتقل شود"),"existing physical location is shown explicitly and requires a separate transfer decision");
            await Input("SHOULD-NOT-SAVE");check(await db.SalesLists.AsNoTracking().AnyAsync(l=>l.Id==manual.Id&&l.StoragePosition=="LOC-A"),"a location cannot change before the administrator accepts transfer");
            await Select(sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locmove:"))["invoiceadmin:ready:".Length..]);
            await Input(new string('x',151));check(sent.Texts.Last().Contains("۱۵۰"),"location transfer rejects overlong physical positions");
            await Input("LOC-B");var locationMoveSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locsave:"))["invoiceadmin:ready:".Length..];
            await service.SetPositionAsync(manual.Id,"LOC-OTHER-ADMIN",Owner);
            await Select(locationMoveSave);check(sent.Texts.Last().Contains("تغییر کرده")&&await db.SalesLists.AsNoTracking().AnyAsync(l=>l.Id==manual.Id&&l.StoragePosition=="LOC-OTHER-ADMIN"),"concurrent location changes invalidate old confirmation instead of overwriting another administrator's move");
            await Select("register-position");await Input(manual.InventoryCode.Value.ToString());await Select("locpick:"+manual.Id.ToString("N"));
            await Select(sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locok:"))["invoiceadmin:ready:".Length..]);
            await Select(sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locmove:"))["invoiceadmin:ready:".Length..]);await Input("LOC-C");
            await Select(sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:locsave:"))["invoiceadmin:ready:".Length..]);
            check(await db.InventoryPositionChanges.AnyAsync(v=>v.SalesListId==manual.Id&&v.PreviousPosition=="LOC-OTHER-ADMIN"&&v.Position=="LOC-C"&&v.AdminUserId==Owner),"approved transfer records the real previous and new physical locations with the acting administrator");
            check(await db.NotificationOutbox.CountAsync()==locationJobCount&&!ReadyInventoryPresentation.Caption(await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==manual.Id)).Contains("LOC-C"),"location registration creates no publication, invoice or customer notification and remains absent from public stock captions");
            db.ChangeTracker.Clear();ready=await db.SalesLists.SingleAsync(v=>v.Id==ready.Id);
            ready.TelegramChannelId="-100222333";ready.TelegramMessageId=8;await db.SaveChangesAsync();
            var promo=new FlashSalesController(db,Options.Create(new TelegramOptions{OwnerUserId=Owner.ToString(),ReadyInventoryChannelId="-100222333",InvoiceFailureChatId="-100444555"}));var campaign=Guid.NewGuid();
            check(await promo.Create(new(campaign,ready.Id,50,120,null,Owner,10000),default)is OkObjectResult,"owner can activate a promotion for a published ready package");
            var quote=await FlashSalePricing.QuoteAsync(db,ready,DateTime.UtcNow);
            check(quote.PricePerMl==5000&&(await FlashSalePricing.QuoteAsync(db,manual,DateTime.UtcNow)).PromotionId==null,"ready promotion affects exactly one package, never another package");
            var json=System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)await promo.Get(campaign,default)).Value);
            check(json.Contains("https://t.me/c/222333/8")&&!json.Contains("shelf-3"),"inventory promotion uses real inventory post URL without exposing physical position");
            var request=await Request(ready,"one");var competing=await Request(ready,"two");
            await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(request.Id,request.TelegramUserId);
            var refused=false;try{await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(competing.Id,competing.TelegramUserId);}catch(InvalidOperationException){refused=true;}
            check(refused,"second customer cannot reserve a ready package already reserved by the first");
            db.ChangeTracker.Clear();
            await db.SalesListRequests.Where(v=>v.Id==request.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.ConfirmedAt,DateTime.UtcNow.AddDays(-60)));
            await db.SalesLists.Where(v=>v.Id==reference.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.PricePerMl,12000m));
            await InventoryReferencePricing.SynchronizeOneAsync(db,ready.Id);
            await promo.End(campaign,new(Owner),default);
            var billingSender=DispatchProxy.Create<ISender,ReadyBillingSender>();((ReadyBillingSender)(object)billingSender).Db=db;
            var billing=new InvoiceIssuanceService(db,billingSender);
            var result=await billing.IssueCompletedListsAsync([ready.Id],Owner.ToString());
            var again=await billing.IssueCompletedListsAsync([ready.Id],Owner.ToString());
            check(result.BatchId==again.BatchId&&await db.OrderItems.CountAsync(v=>v.SalesListId==ready.Id)==1,"repeated admin billing approval returns the same invoice without duplicate orders");
            var item=await db.OrderItems.AsNoTracking().Include(v=>v.Order).SingleAsync(v=>v.SalesListId==ready.Id);
            check(item.PerfumePricePerMl==5000&&item.BottlePrice==2000&&item.LineTotal==17000&&item.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&item.Order!.IsInventory&&item.Order.Status==OrderStatus.ReadyToShip,
                "admin can invoice a sixty-day-old reservation after offer expiry while preserving discount and ready-to-ship status");
            check(result.ProductionCopies.Count==0&&(await billing.GetProductionCopiesAsync([ready.Id])).Count==0,"ready invoices never create decant or label production copies, including later reprints");
            var invoice=await db.Invoices.AsNoTracking().SingleAsync(v=>v.OrderId==item.OrderId);
            check(await db.NotificationOutbox.AnyAsync(v=>v.EventType=="InvoiceIssued"&&v.Payload.Contains(invoice.InvoiceNumber)),"approved ready inventory invoice enters existing durable customer notification flow");
            var integration=await db.NotificationOutbox.SingleAsync(v=>v.Channel=="N8n"&&v.OrderId==item.OrderId&&v.EventType=="InvoiceIssued");
            check(System.Text.Json.JsonDocument.Parse(integration.Payload).RootElement.GetProperty("IsInventory").GetBoolean()&&!integration.Payload.Contains("shelf-3"),
                "invoice integration identifies ready inventory without leaking storage position");
            var halfId=Guid.NewGuid();ids.Add(halfId);
            var half=await service.CreateAsync(new(halfId,null,marker+"half","fixture-photo",100000,1.5m,bottle.Id,2000,"half-shelf",Owner));perfumes.Add(half.PerfumeId);
            check(half.TotalVolume==1.5m&&half.CanReserveVolume(1.5m)&&!half.CanReserveVolume(1)&&!half.CanReserveVolume(0.5m),"half-ml inventory remains a single indivisible 1.5 ml package");
            var halfButtons=((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)typeof(TelegramWebhookController).GetMethod("BuildChannelVolumeButtons",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[half,null])!).SelectMany(v=>v).ToArray();
            check(halfButtons.Length==1&&halfButtons[0].CallbackData!.EndsWith(":1.5"),"fractional ready purchase button encodes exact invariant volume");
            var halfRequest=await Request(half,"half");await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(halfRequest.Id,halfRequest.TelegramUserId);
            await billing.IssueCompletedListsAsync([half.Id],Owner.ToString());
            var halfItem=await db.OrderItems.AsNoTracking().SingleAsync(v=>v.SalesListId==half.Id);
            check(halfItem.RequestedVolumeMl==1.5m&&halfItem.PerfumeAmount==150000&&halfItem.LineTotal==152000,"1.5 ml at 100000 per ml invoices as 150000 perfume plus unchanged bottle cost");
            var halfInvoice=await db.Invoices.Include(v=>v.Order).SingleAsync(v=>v.OrderId==halfItem.OrderId);
            check(halfInvoice.DeliveryStatus==InvoiceDeliveryStatus.NeedsManualAction,"inventory invoice with no customer group waits for group connection");
            await db.NotificationOutbox.Where(v=>v.OrderId==halfItem.OrderId&&v.Channel=="N8n").ExecuteUpdateAsync(v=>v.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            db.ChangeTracker.Clear();
            var tracker=new TelegramGroupMembershipTracker(db,sender,Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramGroupMembershipTracker>.Instance);
            var customerChat=-1000000000000L-Random.Shared.Next(100000000,900000000);
            var connected=await tracker.LinkByInvoiceAsync(new(customerChat,"supergroup",Title:marker),halfInvoice.InvoiceNumber,default);
            var retry=await db.NotificationOutbox.SingleAsync(v=>v.OrderId==halfItem.OrderId&&v.Channel=="N8n"&&v.Status==NotificationOutboxStatus.Pending);
            using(var retried=System.Text.Json.JsonDocument.Parse(retry.Payload))
                check(connected.QueuedInvoiceCount==1&&retried.RootElement.GetProperty("IsInventory").GetBoolean()&&retried.RootElement.GetProperty("Delivery").GetProperty("ChatId").GetString()==customerChat.ToString(),"connecting a customer group preserves inventory identity in the regenerated invoice payload");
            var legacy=System.Text.Json.Nodes.JsonNode.Parse(retry.Payload)!.AsObject();legacy.Remove("IsInventory");retry.Payload=legacy.ToJsonString();retry.Status=NotificationOutboxStatus.Processed;await db.SaveChangesAsync();
            halfInvoice=await db.Invoices.Include(v=>v.Order).SingleAsync(v=>v.OrderId==halfItem.OrderId);
            var destination=await db.CustomerTelegramGroups.SingleAsync(v=>v.CustomerId==halfInvoice.Order!.CustomerId);
            var regenerated=await (Task<bool>)typeof(TelegramWebhookController).GetMethod("QueueInvoicePdfRegenerationAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[halfInvoice,destination,CancellationToken.None])!;
            var regeneratedJob=await db.NotificationOutbox.SingleAsync(v=>v.OrderId==halfItem.OrderId&&v.Channel=="N8n"&&v.Status==NotificationOutboxStatus.Pending);
            check(regenerated&&System.Text.Json.JsonDocument.Parse(regeneratedJob.Payload).RootElement.GetProperty("IsInventory").GetBoolean()&&halfInvoice.TotalAmount==152000,"manual PDF regeneration restores missing inventory flag from saved order without repricing invoice");
            var invalidHalf=false;try{await service.CreateAsync(new(Guid.NewGuid(),null,marker+"invalid","fixture-photo",100000,1.25m,bottle.Id,2000,"half-shelf",Owner));}catch(InvalidOperationException){invalidHalf=true;}
            check(invalidHalf,"inventory rejects quarter-ml quantities instead of rounding money or stock");
            Task Remove(SalesList value)=>(Task)typeof(TelegramWebhookController).GetMethod("RemoveInvoicedReadyInventoryPostAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[value,CancellationToken.None])!;
            var deletesBefore=sent.Deletes.Count;
            await Remove(new SalesList{Id=manual.Id,InventoryCode=manual.InventoryCode,IsInventoryOffer=true,Status=SalesListStatus.Full,TelegramMessageId=88,TelegramChannelId="-100222333"});
            await Remove(new SalesList{Id=reference.Id,Status=SalesListStatus.Invoiced,TelegramMessageId=77,TelegramChannelId="-1001236168661"});
            check(sent.Deletes.Count==deletesBefore,"reservations awaiting admin approval and ordinary lists never delete inventory-channel messages");
            check(await db.NotificationOutbox.CountAsync(v=>v.Channel=="ReadyInventoryRefresh"&&v.EventType=="InventoryInvoiced"&&v.Recipient==ready.Id.ToString())==1,
                "successful invoice queues one durable inventory removal job and replayed approval cannot duplicate it");
            var publishedReady=await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==ready.Id);
            sent.DeleteResult=new(false,Error:"Too Many Requests");var removalFailed=false;
            try{await Remove(publishedReady);}catch(InvalidOperationException){removalFailed=true;}
            check(removalFailed&&(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==ready.Id)).TelegramMessageId==8,
                "temporary Telegram deletion failure retains exact post identity for a retry");
            sent.DeleteResult=new(false,Error:"429"){IsRetryable=true,RetryAfterSeconds=120};
            var fullCooldown=false;try{await Remove(publishedReady);}catch(InventoryPostRetryException e){fullCooldown=e.RetryAfterSeconds==120;}
            check(fullCooldown&&!await db.NotificationOutbox.AnyAsync(n=>n.EventType==ReadyInventoryRemoval.ManualAlertEvent&&n.Payload.Contains(ready.Id.ToString())),
                "first transient inventory delete rejection preserves the full server cooldown without an immediate manual alert");
            sent.DeleteResult=new(false,Error:"Bad Request: message can't be deleted");sent.EditResult=new(false,Error:"429");
            var withdrawalRetry=false;try{await Remove(publishedReady);}catch(InvalidOperationException){withdrawalRetry=true;}
            var retiredBinding=ReadyInventoryRemoval.Binding(ready.Id,"-100222333",8);
            check(withdrawalRetry&&!await db.NotificationOutbox.AnyAsync(n=>n.Channel==ReadyInventoryRemoval.RetirementChannel&&n.Recipient==retiredBinding),
                "restricted deletion with a failed sold-caption edit remains retryable without claiming the offer was withdrawn");
            sent.EditResult=new(true);await Remove(publishedReady);
            check(sent.Edits.Last().Chat=="-100222333"&&sent.Edits.Last().Message==8&&sent.Edits.Last().Buttons==0&&sent.Edits.Last().Text.Contains("فروخته شد")&&
                !sent.Edits.Last().Text.Contains("PRIVATE-POSITION")&&(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==ready.Id)).TelegramMessageId==8,
                "old inventory post that Telegram refuses to delete becomes visibly sold with no purchase buttons and retains its exact audit identity");
            check(await db.Invoices.AnyAsync(v=>v.Id==invoice.Id&&v.TotalAmount==17000&&v.Status!=ZibasheERP.Domain.Enums.InvoiceStatus.Cancelled)&&
                await db.OrderItems.AnyAsync(v=>v.Id==item.Id&&!v.IsDeleted),"public post deletion limits never reverse successful invoice amounts or item ownership");
            var manualAlert=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.EventType==ReadyInventoryRemoval.ManualAlertEvent&&n.Payload.Contains(ready.Id.ToString()));
            var manualText=ZibasheERP.Application.Notifications.TelegramNotificationMessageFormatter.Format(manualAlert.EventType,manualAlert.Payload);
            check(manualAlert.Channel=="Telegram"&&manualAlert.Recipient=="-100444555"&&manualAlert.OrderId==null&&manualText.Contains("فاکتور را دوباره صادر نکنید")&&
                manualText.Contains(ready.DisplayCode.ToString())&&manualText.Contains("https://t.me/c/222333/8")&&!manualText.Contains("PRIVATE-POSITION"),
                "restricted inventory deletion queues a clear manual-cleanup notice to the invoice-failure group with the exact post link and no customer financial mutation");
            await ReadyInventoryRemoval.QueueManualAlertAsync(db,ready.Id,"-100444555",true,true);
            check(await db.NotificationOutbox.CountAsync(n=>n.EventType==ReadyInventoryRemoval.ManualAlertEvent&&n.Payload.Contains(ready.Id.ToString()))==1,
                "repeated cleanup failures for the same exact post cannot queue duplicate manual notices");
            var worker=new TelegramOutboxWorker(null!,sender,Options.Create(new TelegramOptions{InvoiceFailureChatId="-100444555"}),Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramOutboxWorker>.Instance);
            await (Task<TelegramSendResult>)typeof(TelegramOutboxWorker).GetMethod("SendNotificationAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(worker,[manualAlert,manualAlert.Recipient,db,CancellationToken.None])!;
            check(sent.Urls.Contains("https://t.me/c/222333/8")&&sent.Texts.Last().Contains("حذف دستی"),"manual-cleanup alert includes a working open-post button in the standard Telegram notification transport");
            await db.NotificationOutbox.Where(n=>n.Id==manualAlert.Id).ExecuteDeleteAsync();db.ChangeTracker.Clear();
            check(await ReadyInventoryRemoval.RecoverManualAlertsAsync(db,"-100444555")==1&&await ReadyInventoryRemoval.RecoverManualAlertsAsync(db,"-100444555")==0,
                "old retirement receipts and an interrupted alert enqueue recover one operator notice without repeating it on every maintenance pass");
            var restoredAlert=await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==manualAlert.Id);
            check(ZibasheERP.Application.Notifications.TelegramNotificationMessageFormatter.Format(restoredAlert.EventType,restoredAlert.Payload).Contains("دکمه‌های خرید برداشته"),
                "recovered already-withdrawn inventory notice accurately reports that purchase buttons are gone");
            var retiredDeletes=sent.Deletes.Count;var retiredEdits=sent.Edits.Count;await Remove(publishedReady);
            check(sent.Deletes.Count==retiredDeletes&&sent.Edits.Count==retiredEdits,"proved withdrawal is not re-deleted or re-edited on every maintenance pass");
            var originalCleanupIds=await db.NotificationOutbox.Where(n=>n.Channel=="ReadyInventoryRefresh"&&n.Recipient==ready.Id.ToString()).Select(n=>n.Id).ToArrayAsync();
            await db.NotificationOutbox.Where(n=>originalCleanupIds.Contains(n.Id)).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            check(await ReadyInventoryRemoval.RecoverAsync(db)==0,"maintenance does not endlessly recreate cleanup jobs for the exact already-withdrawn old post");
            await db.SalesLists.Where(v=>v.Id==ready.Id).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.TelegramMessageId,18L));
            check(await ReadyInventoryRemoval.RecoverAsync(db)==1,"a late receipt for a different message still queues cleanup despite an older withdrawn-post receipt");
            // Restore this synthetic fixture so the original successful-delete checks run too.
            await db.NotificationOutbox.Where(n=>n.Channel=="ReadyInventoryRefresh"&&n.Recipient==ready.Id.ToString()&&!originalCleanupIds.Contains(n.Id)).ExecuteDeleteAsync();
            await db.NotificationOutbox.Where(n=>n.Channel==ReadyInventoryRemoval.RetirementChannel&&n.Recipient==retiredBinding).ExecuteDeleteAsync();
            await db.NotificationOutbox.Where(n=>originalCleanupIds.Contains(n.Id)).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Pending));
            await db.SalesLists.Where(v=>v.Id==ready.Id).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.TelegramMessageId,8L));db.ChangeTracker.Clear();
            sent.DeleteResult=new(true);await Remove(publishedReady);
            check(sent.Deletes.Last()==("-100222333",8L)&&(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==ready.Id)).TelegramMessageId==null&&
                await db.Invoices.AnyAsync(v=>v.Id==invoice.Id&&v.TotalAmount==17000)&&await db.OrderItems.AnyAsync(v=>v.Id==item.Id),
                "invoicing removes only the package publication while preserving invoice amounts and customer order history");
            var afterRemoval=sent.Deletes.Count;await Remove(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==ready.Id));
            check(sent.Deletes.Count==afterRemoval,"repeated removal of a deleted inventory publication makes no further Telegram calls");
            var publication=new InventoryOffersController(db,Options.Create(new TelegramOptions{ReadyInventoryChannelId="-100222333",InvoiceFailureChatId="-100444555"}),billing);
            check(await publication.Publication(ready.Id,null,default)is ConflictResult,"a sold package cannot be published again by an old publication job");
            await db.NotificationOutbox.Where(v=>v.Channel=="ReadyInventoryRefresh"&&v.Recipient==half.Id.ToString()).ExecuteUpdateAsync(v=>v.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed));
            await db.SalesLists.Where(v=>v.Id==half.Id).ExecuteUpdateAsync(v=>v.SetProperty(n=>n.TelegramMessageId,9L).SetProperty(n=>n.TelegramChannelId,"-100222333"));
            check(await ReadyInventoryRemoval.RecoverAsync(db)==1&&await ReadyInventoryRemoval.RecoverAsync(db)==0,
                "maintenance recovers old invoiced posts and late publication receipts without duplicating pending deletion jobs");
            sent.DeleteResult=new(false,Error:"Bad Request: message to delete not found");
            await Remove(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==half.Id));
            check((await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==half.Id)).TelegramMessageId==null,
                "already missing inventory post is treated as successful cleanup after an interrupted delete");
            typeof(TelegramWebhookController).GetField("_salesListRequestRepository",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,new SalesListRequestRepository(db));
            typeof(TelegramWebhookController).GetField("_invoiceIssuanceService",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,billing);
            typeof(TelegramWebhookController).GetField("_options",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,new TelegramOptions{OwnerUserId=Owner.ToString(),AdminChatId="-100123",ReadyInventoryChannelId="-100222333",InvoiceFailureChatId="-100444555"});
            var buyerUsername="buyer_"+Guid.NewGuid().ToString("N")[..15];
            await db.Customers.Where(v=>v.Id==halfInvoice.Order!.CustomerId).ExecuteUpdateAsync(v=>v.SetProperty(c=>c.Username,buyerUsername));
            db.ChangeTracker.Clear();
            sent.AllowAccountingAdmin=true;
            var admin=Owner+1;
            var waitingParcel=Guid.NewGuid();
            await db.OrderItems.Where(v=>v.Id==halfItem.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.ShippingRequestId,(Guid?)waitingParcel).SetProperty(i=>i.ShippingRequestedAt,(DateTime?)DateTime.UtcNow.AddHours(-1)));
            db.ChangeTracker.Clear();
            var purchaseId=Guid.NewGuid();ids.Add(purchaseId);
            var purchase=await service.CreateAsync(new(purchaseId,null,marker+"adminbuy","fixture-photo",100000,1.5m,bottle.Id,2000,"buy-shelf",admin));perfumes.Add(purchase.PerfumeId);
            await Select("buy",admin);await Input(purchase.InventoryCode.ToString(),user:admin);await Input("@"+buyerUsername,user:admin);
            check(sent.Texts.Last().Contains("152,000")&&sent.Texts.Last().Contains("گروه حسابداری متصل")&&!await db.SalesListRequests.AnyAsync(v=>v.SalesListId==purchase.Id),
                "accounting admin can preview an inventory purchase for a named customer without creating a reservation or invoice");
            var purchaseSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:buy-save:"));
            await Select(purchaseSave["invoiceadmin:ready:".Length..],admin);await Select(purchaseSave["invoiceadmin:ready:".Length..],admin);
            var purchaseItem=await db.OrderItems.AsNoTracking().SingleAsync(v=>v.SalesListId==purchase.Id);
            check(purchaseItem.ShippingRequestId==waitingParcel&&purchaseItem.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&purchaseItem.ShippedAt==null,
                "actual inventory invoice issuance automatically adds a newly billed package to the customer's existing open shipping request");
            var purchaseEvent=await db.NotificationOutbox.SingleAsync(v=>v.OrderId==purchaseItem.OrderId&&v.Channel=="N8n"&&v.EventType=="InvoiceIssued");
            using(var payload=System.Text.Json.JsonDocument.Parse(purchaseEvent.Payload))
                check(purchaseItem.RequestedVolumeMl==1.5m&&purchaseItem.LineTotal==152000&&payload.RootElement.GetProperty("Delivery").GetProperty("ChatId").GetString()==customerChat.ToString()&&
                    (await db.SalesListRequests.SingleAsync(v=>v.SalesListId==purchase.Id)).CreatedByAdmin,
                    "confirmed admin inventory purchase invoices the selected customer group with exact half-ml amount and a single order despite replay");
            var numericId=(9000000000L+Random.Shared.Next(100000000,900000000)).ToString();
            var numericOfferId=Guid.NewGuid();ids.Add(numericOfferId);
            var numericOffer=await service.CreateAsync(new(numericOfferId,null,marker+"numeric","fixture-photo",100000,0.5m,bottle.Id,2000,"buy-shelf",admin));perfumes.Add(numericOffer.PerfumeId);
            await Select("buy",admin);await Input(numericOffer.InventoryCode.ToString(),user:admin);await Input(numericId,user:admin);
            check(sent.Texts.Last().Contains("متصل نیست")&&sent.Texts.Last().Contains(numericId),"numeric buyer with no connected customer group receives an explicit delivery warning before admin approval");
            var numericSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:buy-save:"));await Select(numericSave["invoiceadmin:ready:".Length..],admin);
            var numericItem=await db.OrderItems.AsNoTracking().Include(v=>v.Order).ThenInclude(v=>v!.Customer).SingleAsync(v=>v.SalesListId==numericOffer.Id);
            check(numericItem.Order!.Customer!.TelegramId==numericId&&numericItem.LineTotal==52000&&
                (await db.Invoices.SingleAsync(v=>v.OrderId==numericItem.OrderId)).DeliveryStatus==InvoiceDeliveryStatus.NeedsManualAction,
                "admin can invoice an inventory buyer by numeric Telegram ID without redirecting the invoice to the administrator");
            typeof(TelegramWebhookController).GetField("_salesListRepository",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,new SalesListRepository(db));
            var bundleOffers=new List<SalesList>();
            foreach(var suffix in new[]{"bundle-one","bundle-two","bundle-other","bundle-late"})
            {
                var id=Guid.NewGuid();ids.Add(id);
                var offer=await service.CreateAsync(new(id,null,marker+suffix,"fixture-photo",100000,0.5m,bottle.Id,2000,"bundle-private",Owner));
                perfumes.Add(offer.PerfumeId);bundleOffers.Add(offer);
            }
            foreach(var index in new[]{0,1,2})
            {
                var req=await Request(bundleOffers[index],index==2?"different-buyer":"bundle-buyer");
                await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(req.Id,req.TelegramUserId);
            }
            db.ChangeTracker.Clear();
            var previewStart=sent.Texts.Count;
            await Select("review:"+bundleOffers[0].Id.ToString("N"),admin);
            var bundlePreview=string.Join("\n",sent.Texts.Skip(previewStart));
            check(bundlePreview.Contains("104,000")&&bundlePreview.Contains($"کد موجودی {bundleOffers[1].DisplayCode}")&&
                !bundlePreview.Contains($"کد موجودی {bundleOffers[2].DisplayCode}")&&!await db.OrderItems.AnyAsync(v=>v.SalesListId==bundleOffers[0].Id),
                "inventory invoice preview includes both outstanding items of one buyer with exact total, excludes another buyer and does not invoice before approval");
            var bundleSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:approve-bundle:"));
            await Select(bundleSave["invoiceadmin:ready:".Length..],Owner);
            check(!await db.OrderItems.AnyAsync(v=>v.SalesListId==bundleOffers[0].Id),"another admin cannot approve a different administrator's inventory invoice preview");
            var lateRequest=await Request(bundleOffers[3],"bundle-buyer");
            await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(lateRequest.Id,lateRequest.TelegramUserId);
            db.ChangeTracker.Clear();
            var publishedBundleIds=bundleOffers.Take(2).Select(v=>v.Id).ToArray();
            for(var publicationIndex=0;publicationIndex<publishedBundleIds.Length;publicationIndex++)
            {
                var publicationId=publishedBundleIds[publicationIndex];var publicationMessage=201L+publicationIndex;
                await db.SalesLists.Where(v=>v.Id==publicationId).ExecuteUpdateAsync(s=>s.SetProperty(v=>v.TelegramMessageId,publicationMessage).SetProperty(v=>v.TelegramChannelId,"-100222333"));
            }
            sent.DeleteResult=new(false,Error:"Bad Request: message can't be deleted");var deletionsBeforeBilling=sent.Deletes.Count;
            await Select(bundleSave["invoiceadmin:ready:".Length..],admin);
            check(sent.Deletes.Count==deletionsBeforeBilling&&sent.Texts.Last().Contains("فاکتور تجمیعی")&&sent.Texts.Last().Contains("صادر شد"),
                "inventory billing acknowledges committed invoices immediately and defers Telegram cleanup failures to the durable queue");
            sent.DeleteResult=new(true);
            var bundledItems=await db.OrderItems.AsNoTracking().Where(v=>v.SalesListId==bundleOffers[0].Id||v.SalesListId==bundleOffers[1].Id).ToArrayAsync();
            check(bundledItems.Length==2&&bundledItems.Select(v=>v.OrderId).Distinct().Count()==1&&bundledItems.Sum(v=>v.LineTotal)==104000&&
                !await db.OrderItems.AnyAsync(v=>v.SalesListId==bundleOffers[2].Id||v.SalesListId==bundleOffers[3].Id),
                "one approved inventory invoice contains both half-ml packages and preserves bottle prices while excluding other customers and requests arriving after preview");
            var changedSnapshotRejected=false;
            try {await billing.IssueCompletedListsAsync([bundleOffers[2].Id],admin.ToString(),default,[Guid.NewGuid()]);}
            catch(InvalidOperationException){changedSnapshotRejected=true;}
            check(changedSnapshotRejected&&!await db.OrderItems.AnyAsync(v=>v.SalesListId==bundleOffers[2].Id),
                "changed inventory request identity invalidates the invoice preview atomically instead of charging an unreviewed replacement reservation");
            var bundleOrder=await db.Orders.SingleAsync(v=>v.Id==bundledItems[0].OrderId);
            var bundleRepeat=await billing.IssueCompletedListsAsync(bundleOffers.Take(2).Select(v=>v.Id).ToArray(),admin.ToString());
            check(bundleRepeat.BatchId==bundleOrder.InvoiceIssuanceBatchId&&await db.Invoices.CountAsync(v=>v.OrderId==bundleOrder.Id)==1&&bundleOrder.IsInventory&&bundleOrder.Status==OrderStatus.ReadyToShip,
                "replayed consolidated inventory billing returns the same invoice without duplicating orders or losing ready-to-ship inventory identity");
            check(await db.NotificationOutbox.CountAsync(v=>v.Channel=="ReadyInventoryRefresh"&&v.EventType=="InventoryInvoiced"&&
                (v.Recipient==bundleOffers[0].Id.ToString()||v.Recipient==bundleOffers[1].Id.ToString()))==2,
                "consolidated inventory invoice queues removal of each sold public offer");
            var staleId=Guid.NewGuid();ids.Add(staleId);
            var stale=await service.CreateAsync(new(staleId,null,marker+"stale","fixture-photo",100000,1.5m,bottle.Id,2000,"buy-shelf",admin));perfumes.Add(stale.PerfumeId);
            await Select("buy",admin);await Input(stale.InventoryCode.ToString(),user:admin);await Input("@"+buyerUsername,user:admin);
            var staleSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:buy-save:"));
            await Select("buy-identity",admin);await Select(staleSave["invoiceadmin:ready:".Length..],admin);
            check(!await db.SalesListRequests.AnyAsync(v=>v.SalesListId==stale.Id),"editing the buyer invalidates the prior inventory purchase confirmation");
            await Input("@"+buyerUsername,user:admin);var changedPriceSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:buy-save:"));
            await db.SalesLists.Where(v=>v.Id==stale.Id).ExecuteUpdateAsync(v=>v.SetProperty(l=>l.PricePerMl,120000m));
            await Select(changedPriceSave["invoiceadmin:ready:".Length..],admin);
            check(!await db.OrderItems.AnyAsync(v=>v.SalesListId==stale.Id)&&(await db.SalesLists.AsNoTracking().SingleAsync(v=>v.Id==stale.Id)).ReservedVolume==0,
                "a price increase after admin purchase preview requires a new confirmation and cannot silently reprice or reserve stock");
            await Select("buy-identity",admin);await Input(numericId,user:admin);
            var correctedBuyerSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:buy-save:"));db.ChangeTracker.Clear();await Select(correctedBuyerSave["invoiceadmin:ready:".Length..],admin);
            var correctedBuyerItem=await db.OrderItems.AsNoTracking().Include(v=>v.Order).SingleAsync(v=>v.SalesListId==stale.Id);
            check(correctedBuyerItem.Order!.CustomerId==numericItem.Order.CustomerId&&correctedBuyerItem.LineTotal==182000&&
                await db.SalesListRequests.AnyAsync(v=>v.SalesListId==stale.Id&&v.TelegramUsername==buyerUsername&&v.Status==SalesListRequestStatus.Cancelled),
                "changing buyer after a failed confirmation cancels the old pending request and invoices only the newly approved customer");
            sent.AllowAccountingAdmin=false;await Select("buy",admin);
            check(sent.Texts.Last().Contains("ادمین"),"inventory purchase entry rechecks accounting-administrator access");
            var report=await service.ReportAsync(10);
            check(report.InvoicedPackages>=1&&report.UnpaidInvoices>=1&&report.Outstanding>=17000,"inventory report counts invoiced packages and outstanding issued invoices");
            db.Payments.Add(new(){Id=Guid.NewGuid(),OrderId=item.OrderId,Amount=7000,Status=PaymentStatus.Confirmed,CreatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
            var partial=await service.ReportAsync(10);check(partial.Outstanding==report.Outstanding-7000,"partial payment reduces inventory report outstanding amount without losing unpaid count");
            var expiredRequest=await Request(manual,"expired");await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(expiredRequest.Id,expiredRequest.TelegramUserId);
            db.ChangeTracker.Clear();await db.SalesListRequests.Where(v=>v.Id==expiredRequest.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.ConfirmedAt,DateTime.UtcNow.AddMinutes(-31)));
            db.ChangeTracker.Clear();
            check((await db.SalesLists.SingleAsync(v=>v.Id==manual.Id)).Status==SalesListStatus.Full&&
                (await db.SalesListRequests.SingleAsync(v=>v.Id==expiredRequest.Id)).Status==SalesListRequestStatus.Confirmed,
                "an old confirmed inventory purchase remains reserved indefinitely pending admin action");
            var lateCompeting=await Request(manual,"later");var stillRefused=false;
            try{await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(lateCompeting.Id,lateCompeting.TelegramUserId);}catch(InvalidOperationException){stillRefused=true;}
            check(stillRefused,"another customer cannot take an old reservation while its original request is active");
            await service.RejectAsync(manual.Id);db.ChangeTracker.Clear();
            check((await db.SalesLists.SingleAsync(v=>v.Id==manual.Id)).ReservedVolume==0&&(await db.SalesListRequests.SingleAsync(v=>v.Id==expiredRequest.Id)).Status==SalesListRequestStatus.Cancelled,
                "only admin rejection releases an unbilled inventory purchase reservation");
            var billedRejected=false;try{await service.RejectAsync(ready.Id);}catch(InvalidOperationException){billedRejected=true;}
            check(billedRejected,"admin rejection cannot silently reopen or erase an invoiced package");
            check(await db.NotificationOutbox.AnyAsync(v=>v.EventType=="ReadyInventoryInvoiceReport"&&v.Payload.Contains(ready.Id.ToString())),"payment-group report is queued atomically with ready invoice even if webhook stops immediately afterward");

            // Financial reporting must be private even for otherwise authorized accounting admins.
            sent.AllowAccountingAdmin=true;sent.Buttons.Clear();
            await Select("finance",admin);await Select("finance:7",admin);
            check(sent.Texts.Last().Contains("فقط برای مدیر")&&!sent.Buttons.Any(v=>v.StartsWith("invoiceadmin:ready:finance:")),
                "accounting admin cannot open financial report or forge a period callback");
            var mainMenu=typeof(TelegramWebhookController).GetMethod("SendInvoiceAdminMenuAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            sent.Buttons.Clear();await (Task)mainMenu.Invoke(controller,[admin,null,CancellationToken.None,"accountantzibashe"])!;
            check(!sent.Buttons.Contains("invoiceadmin:ready:finance"),"financial report button is absent from accounting-admin menu");
            sent.Buttons.Clear();await (Task)mainMenu.Invoke(controller,[Owner,null,CancellationToken.None,"zahraa_frj"])!;
            check(sent.Buttons.Contains("invoiceadmin:ready:finance"),"financial inventory report appears only in configured manager menu");
            await Select("finance");check(new[]{7,15,30}.All(day=>sent.Buttons.Contains("invoiceadmin:ready:finance:"+day)),"manager can choose 7, 15 and 30 day financial periods");
            await Select("finance:7");check(sent.Texts.Last().Contains("فاکتور شده:")&&sent.Texts.Last().Contains("پرداخت شده:")&&sent.Texts.Last().Contains("در انتظار پرداخت:")&&sent.Texts.Last().Contains("فروش نرفته:"),"manager receives financial sales and unsold inventory values privately");
            var invalidPeriod=false;try{await service.FinancialReportAsync(10);}catch(InvalidOperationException){invalidPeriod=true;}
            check(invalidPeriod,"financial report rejects unsupported periods");
            var financial=await service.FinancialReportAsync(7);
            check(financial.SalesAmount==financial.ReceivedAmount+financial.OutstandingAmount&&financial.ReceivedAmount>=7000,
                "financial report reconciles sold amounts with confirmed partial payments and outstanding debt");
            var financialHalfItem=await db.OrderItems.AsNoTracking().SingleAsync(v=>v.SalesListId==half.Id&&!v.IsDeleted);
            var financialHalfInvoice=await db.Invoices.AsNoTracking().SingleAsync(v=>v.OrderId==financialHalfItem.OrderId&&!v.IsDeleted);
            await db.Invoices.Where(v=>v.Id==financialHalfInvoice.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.IssuedAt,DateTime.UtcNow.AddDays(-10)));
            var seven=await service.FinancialReportAsync(7);var fifteen=await service.FinancialReportAsync(15);
            check(seven.SalesAmount==financial.SalesAmount-financialHalfItem.LineTotal&&fifteen.SalesAmount==financial.SalesAmount,
                "sales date filter uses invoice issuance and preserves exact half-ml line amounts including bottle cost");
            await db.Invoices.Where(v=>v.Id==financialHalfInvoice.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.IssuedAt,DateTime.UtcNow.AddDays(-20)));
            check((await service.FinancialReportAsync(15)).SalesAmount==seven.SalesAmount&&(await service.FinancialReportAsync(30)).SalesAmount==financial.SalesAmount,
                "15 and 30 day periods select different invoice cohorts correctly");
            await db.Invoices.Where(v=>v.Id==financialHalfInvoice.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.Status,InvoiceStatus.Cancelled));
            check((await service.FinancialReportAsync(30)).SalesAmount==seven.SalesAmount,"cancelled inventory invoices are excluded from sales totals");
            await db.Invoices.Where(v=>v.Id==financialHalfInvoice.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.Status,financialHalfInvoice.Status).SetProperty(i=>i.IssuedAt,financialHalfInvoice.IssuedAt));
            var stockBefore=await service.FinancialReportAsync(7);
            await db.SalesLists.Where(v=>v.Id==manual.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.CreatedAt,DateTime.UtcNow.AddDays(-60)).SetProperty(i=>i.PricePerMl,i=>i.PricePerMl+100));
            var stockAfter=await service.FinancialReportAsync(7);
            check(stockAfter.UnsoldPackages==stockBefore.UnsoldPackages&&stockAfter.UnsoldValue==stockBefore.UnsoldValue+300,
                "all unsold stock is valued at current price plus bottle cost independent of its registration age");
            await db.SalesLists.Where(v=>v.Id==manual.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.Status,SalesListStatus.Full).SetProperty(i=>i.ReservedVolume,i=>i.TotalVolume));
            var reserved=await service.FinancialReportAsync(7);
            check(reserved.SalesAmount==stockAfter.SalesAmount&&reserved.UnsoldValue==stockAfter.UnsoldValue&&reserved.ReservedPackages==stockAfter.ReservedPackages+1,
                "unbilled indefinite reservations remain unsold and are separately identified without inflating sales");
            await db.SalesLists.Where(v=>v.Id==manual.Id).ExecuteUpdateAsync(v=>v.SetProperty(i=>i.Status,SalesListStatus.Cancelled));
            check((await service.FinancialReportAsync(7)).UnsoldValue==reserved.UnsoldValue-(manual.PricePerMl+100)*manual.TotalVolume-manual.FixedBottlePrice!.Value,
                "cancelled stock offers do not inflate unsold inventory value");

            await Select("manual");await Input(marker+"original");await Input(null,true);await Input("۱۰۰۰۰۰");await Input("۱٫۵");
            check(sent.ButtonLabels.Contains("🧴 باتل اصلی رایگان"),"stock bottle selection includes the free original bottle");
            await Select("original-bottle");
            check(sent.Texts.Last().Contains("جایگاه فیزیکی"),"free original bottle skips bottle-price entry and asks for internal position");
            await Input("original-shelf");await Select("skip-description");
            check(sent.Texts.Last().Contains("150,000")&&sent.Texts.Any(v=>v.Contains("باتل اصلی رایگان — هزینه")),"original-bottle preview charges perfume only and identifies free packaging");
            var originalSave=sent.Buttons.Last(v=>v.StartsWith("invoiceadmin:ready:save:"));await Select(originalSave["invoiceadmin:ready:".Length..]);
            var original=await db.SalesLists.SingleAsync(v=>v.PersianName==marker+"original");ids.Add(original.Id);perfumes.Add(original.PerfumeId);
            check(original.FixedBottleId==ReadyInventoryService.OriginalBottleId&&original.FixedBottlePrice==0,"approved inventory persists a distinct original bottle with zero charge");
            check(!(await new BottleRepository(db).GetActiveAsync()).Any(v=>v.Id==ReadyInventoryService.OriginalBottleId),"original stock packaging is excluded from regular customer bottle choices");
            var originalRequest=await Request(original,"original");originalRequest.BottleId=original.FixedBottleId;originalRequest.BottlePrice=0;await db.SaveChangesAsync();
            await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(originalRequest.Id,originalRequest.TelegramUserId);
            await billing.IssueCompletedListsAsync([original.Id],Owner.ToString());
            var originalItem=await db.OrderItems.AsNoTracking().Include(v=>v.Bottle).SingleAsync(v=>v.SalesListId==original.Id);
            check(originalItem.BottlePrice==0&&originalItem.LineTotal==150000&&originalItem.Bottle?.Name==ReadyInventoryService.OriginalBottleName,
                "stock with original bottle invoices successfully with perfume-only amount and preserved bottle name");
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orders=await db.OrderItems.Where(v=>v.SalesListId!=null&&ids.Contains(v.SalesListId.Value)).Select(v=>v.OrderId).ToArrayAsync();
            var customerIds=await db.Orders.Where(v=>orders.Contains(v.Id)).Select(v=>v.CustomerId).ToArrayAsync();
            var batches=await db.InvoiceIssuanceBatchSalesLists.Where(v=>ids.Contains(v.SalesListId)).Select(v=>v.InvoiceIssuanceBatchId).ToArrayAsync();
            var invoiceNumbers=await db.Invoices.Where(v=>orders.Contains(v.OrderId)).Select(v=>v.InvoiceNumber).ToArrayAsync();
            await db.NotificationOutbox.Where(v=>(v.OrderId.HasValue&&orders.Contains(v.OrderId.Value))||ids.Contains(v.Id)||ids.Select(id=>id.ToString()).Contains(v.Recipient)||v.Payload.Contains(marker)||invoiceNumbers.Any(n=>v.Payload.Contains(n))||ids.Any(id=>v.Payload.Contains(id.ToString()))).ExecuteDeleteAsync();
            await db.Payments.Where(v=>orders.Contains(v.OrderId)).ExecuteDeleteAsync();await db.Invoices.Where(v=>orders.Contains(v.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(v=>orders.Contains(v.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(v=>orders.Contains(v.Id)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatchSalesLists.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();await db.InvoiceIssuanceBatches.Where(v=>batches.Contains(v.Id)).ExecuteDeleteAsync();
            await db.CustomerTelegramGroups.Where(v=>v.Title==marker||customerIds.Contains(v.CustomerId)).ExecuteDeleteAsync();
            await db.Customers.Where(v=>customerIds.Contains(v.Id)||(v.TelegramId!=null&&v.TelegramId.StartsWith(marker))).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();await db.FlashSalePromotions.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();
            await db.TelegramSalesListImports.Where(v=>v.SalesListId!=null&&ids.Contains(v.SalesListId.Value)).ExecuteDeleteAsync();
            await db.InventoryPositionChanges.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(v=>ids.Contains(v.Id)).ExecuteDeleteAsync();
            await db.Perfumes.Where(v=>perfumes.Contains(v.Id)).ExecuteDeleteAsync();await db.Bottles.Where(v=>v.Id==bottle.Id).ExecuteDeleteAsync();
            if(!originalBottleExisted)await db.Bottles.Where(v=>v.Id==ReadyInventoryService.OriginalBottleId).ExecuteDeleteAsync();
        }
    }
}
public class ReadyBillingSender:DispatchProxy
{
    public AppDbContext Db=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>new IssueInvoiceCommandHandler(new InvoiceRepository(Db),new NotificationOutboxRepository(Db),new InvoicePaymentAccountRepository(Db)).Handle((IssueInvoiceCommand)args![0]!,default);
}
