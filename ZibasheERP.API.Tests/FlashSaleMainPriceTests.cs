using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class FlashSaleMainPriceTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var method=typeof(TelegramWebhookController).GetMethod("FormatPromotionChannelSalesListPages",BindingFlags.NonPublic|BindingFlags.Static)!;
        string Caption(SalesList l,FlashSalePricing.Quote q)=>((ValueTuple<string,string?>)method.Invoke(null,[l,Array.Empty<SalesListRequest>(),q])!).Item1;
        var list=new SalesList{Id=Guid.NewGuid(),PublicCode=5867,EnglishName="Test",PersianName="عطر آزمایشی",PricePerMl=764000,TotalVolume=100};
        var promo=Guid.NewGuid();var text=Caption(list,new(564000,promo));
        check(text.Contains("قیمت هر میل: 764,000 تومان\n🔴 <b>قیمت پروموشن هر میل: 564,000 تومان</b>"),"main channel caption places the exact promotion price in bold with a red marker directly below the unchanged ordinary price");
        check(!Caption(list,new(list.PricePerMl,null)).Contains("قیمت پروموشن"),"a caption without an active pricing quote has no promotion-price line");
        foreach(var status in new[]{SalesListStatus.Cancelled,SalesListStatus.Closed,SalesListStatus.Full})
        {
            list.Status=status;
            check(!Caption(list,new(564000,promo)).Contains("قیمت پروموشن")&&!FlashSaleMainPriceDisplay.Snapshot(list,new(564000,promo)).Visible,
                "cancelled or completed list hides a promotion even when its old campaign deadline is still in the future: "+status);
        }
        list.Status=SalesListStatus.Open;list.ReservedVolume=list.TotalVolume;
        check(!Caption(list,new(564000,promo)).Contains("قیمت پروموشن"),"a filled list cannot keep advertising a promotion while rollover is pending");
        list.ReservedVolume=0;
        check(FlashSalePricing.InvoicePrice(new SalesListRequest{FlashSalePromotionId=promo,PerfumePricePerMl=564000},764000)==564000,
            "removing a promotion display preserves the discount accepted by existing confirmed purchases");
        check(Caption(list,new(5000.50m,promo)).Contains("5,000.50 تومان</b>"),"promotion display preserves the billing quote's decimal precision instead of rounding to a different charge");
        list.PersianName=new string('ن',200);list.EnglishName=new string('a',200);list.Accords=new string('b',200);list.TopNotes=new string('ن',200);list.MiddleNotes=new string('م',200);list.BaseNotes=new string('ب',200);list.ProductPageUrl="https://example.com/"+new string('a',200);
        var compact=Caption(list,new(564000,promo));
        check(compact.Contains("🔴 <b>قیمت پروموشن هر میل: 564,000 تومان</b>")&&compact.Length<=1000,"the compact Telegram caption retains the bold promotion amount within its photo-caption budget");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var target=new SqlConnectionStringBuilder(connection);if(target.InitialCatalog!="ZibasheERPCloudDev"||target.DataSource!="127.0.0.1,14333")throw new Exception("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var now=DateTime.UtcNow;var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();var ids=new List<Guid>();
        SalesList Fixture(int code,DateTime open)=>new(){Id=Guid.NewGuid(),PerfumeId=perfume,PublicCode=code,EnglishName="Synthetic promotion display",PricePerMl=764000,TotalVolume=100,Status=SalesListStatus.Open,OpenDate=open,CreatedAt=now,TelegramChannelId="-1001236168661",TelegramMessageId=Random.Shared.Next(9000000,9900000)};
        var origin=Fixture(Random.Shared.Next(4000000,4100000),now.AddDays(-1));var other=Fixture(origin.PublicCode+1,now.AddDays(-1));ids.AddRange([origin.Id,other.Id]);
        var offer=new FlashSalePromotion{Id=Guid.NewGuid(),SalesListId=origin.Id,OriginalPricePerMl=764000,DiscountAmountPerMl=200000,StartsAtUtc=now.AddMinutes(-1),EndsAtUtc=now.AddHours(1),CreatedAt=now};
        try
        {
            db.SalesLists.AddRange(origin,other);db.FlashSalePromotions.Add(offer);await db.SaveChangesAsync();
            var quote=await FlashSalePricing.QuoteAsync(db,origin,now);
            check(quote.PricePerMl==564000&&Caption(origin,quote).Contains("564,000 تومان</b>"),"fixed-amount promotions use the same authoritative quote in the original list caption as customer purchases");
            var targets=await FlashSaleMainPriceDisplay.TargetsAsync(db,now,default);
            check(targets.Contains(origin.Id)&&!targets.Contains(other.Id),"automatic caption refresh selects only published lists covered by an active campaign");
            check(!Caption(origin,await FlashSalePricing.QuoteAsync(db,origin,offer.StartsAtUtc.AddTicks(-1))).Contains("قیمت پروموشن")&&
                !Caption(origin,await FlashSalePricing.QuoteAsync(db,origin,offer.EndsAtUtc)).Contains("قیمت پروموشن"),"promotion display follows inclusive start and exclusive expiry boundaries without requiring a customer click");
            var cycle=Fixture(origin.PublicCode+2,now);cycle.StablePublicCode=origin.DisplayCode;ids.Add(cycle.Id);db.SalesLists.Add(cycle);await db.SaveChangesAsync();
            check((await FlashSaleMainPriceDisplay.TargetsAsync(db,now,default)).Contains(cycle.Id)&&Caption(cycle,await FlashSalePricing.QuoteAsync(db,cycle,now)).Contains("564,000 تومان</b>"),"a newly opened bottle with the stable list code inherits both the campaign price and its original deadline");
            var state=FlashSaleMainPriceDisplay.Snapshot(origin,quote);
            db.NotificationOutbox.Add(new(){Id=FlashSaleMainPriceDisplay.Key(origin.Id),CustomerId=Guid.Empty,Channel=FlashSaleMainPriceDisplay.Channel,EventType="SyntheticPromotionCaption",Recipient=origin.Id.ToString("N"),Payload=JsonSerializer.Serialize(state),Status=NotificationOutboxStatus.Processed,CreatedAt=now});await db.SaveChangesAsync();
            check((await FlashSaleMainPriceDisplay.TargetsAsync(db,offer.EndsAtUtc,default)).Contains(origin.Id),"previously visible promotions remain cleanup targets after their deadline passes");
            origin.Status=SalesListStatus.Cancelled;await db.SaveChangesAsync();
            check((await FlashSaleMainPriceDisplay.TargetsAsync(db,now,default)).Contains(origin.Id)&&
                !Caption(origin,await FlashSalePricing.QuoteAsync(db,origin,now)).Contains("قیمت پروموشن")&&
                !FlashSaleMainPriceDisplay.Snapshot(origin,quote).Visible,
                "a cancelled list remains a cleanup target while its future-dated promotion is still recorded");
            origin.Status=SalesListStatus.Open;await db.SaveChangesAsync();
            offer.CancelledAtUtc=now;await db.SaveChangesAsync();
            check(!Caption(origin,await FlashSalePricing.QuoteAsync(db,origin,now)).Contains("قیمت پروموشن")&&(await FlashSaleMainPriceDisplay.TargetsAsync(db,now,default)).Contains(origin.Id),"manual cancellation removes the offer line and schedules cleanup of its original channel post");
            offer.CancelledAtUtc=null;offer.EndsAtUtc=now.AddHours(1);await db.SaveChangesAsync();await FlashSaleLifecycle.EndAsync(db,offer.Id);
            check(!Caption(origin,await FlashSalePricing.QuoteAsync(db,origin,DateTime.UtcNow)).Contains("قیمت پروموشن"),"ending a promotion early through the existing lifecycle also removes its displayed discounted price");
            check(await db.SalesLists.Where(l=>ids.Contains(l.Id)).AllAsync(l=>l.PricePerMl==764000)&&!await db.SalesListRequests.AnyAsync(r=>ids.Contains(r.SalesListId)),"display synchronization leaves ordinary prices, reservations and invoices unchanged");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>n.Channel==FlashSaleMainPriceDisplay.Channel&&ids.Select(i=>i.ToString("N")).Contains(n.Recipient)).ExecuteDeleteAsync();await db.FlashSalePromotions.Where(p=>ids.Contains(p.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>ids.Contains(l.Id)).ExecuteDeleteAsync();
        }
    }
}
