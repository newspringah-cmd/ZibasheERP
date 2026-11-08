using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal const string PerfumePurchaseReportChannel="PerfumePurchaseReport";
    private const string PurchaseReportPrefix="purchase-report:";
    private sealed record PurchaseReport(long Actor,long Source,int Threshold,Guid[] Lists,int Next=0,
        bool Attempted=false,int Failed=0,int Skipped=0,int WithoutPhoto=0,bool Finished=false);
    private static bool ValidPurchaseThreshold(int value)=>value is 10 or 20 or 30 or 50;
    private static bool PurchaseReportEligible(SalesList list,int threshold)=>ValidPurchaseThreshold(threshold)&&
        !list.IsDeleted&&!list.IsInventoryOffer&&list.Status==SalesListStatus.Open&&list.TotalVolume>0&&
        list.RemainingVolume>0&&list.RemainingVolume*100<list.TotalVolume*threshold;

    private async Task<bool> TryHandlePerfumePurchaseReportCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Data?.StartsWith(PurchaseReportPrefix,StringComparison.Ordinal)!=true)return false;
        var actor=callback.From.Id;
        if(callback.Message is null||callback.Message.Chat.Type!="private"||callback.Message.Chat.Id!=actor||!IsPrimaryOwner(actor))
        {await _sender.AnswerCallbackAsync(callback.Id,"این گزارش فقط برای مدیر اصلی در خصوصی ربات است.",ct,true);return true;}
        var action=callback.Data[PurchaseReportPrefix.Length..];
        if(action=="menu")
        {
            await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
            await _sender.SendInlineKeyboardAsync(actor.ToString(),"🧴 گزارش جهت خرید عطر\nآستانهٔ باقی‌ماندهٔ لیست‌های باز را انتخاب کنید:",
                new[]{10,20,30,50}.Select(t=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"گزارش عطرهای زیر {t} درصد باقیمانده",PurchaseReportPrefix+t)})
                    .Append(new[]{new TelegramInlineButton("↩️ منوی مدیریت","invoiceadmin:menu:main")}).ToArray(),ct);
            return true;
        }
        if(!int.TryParse(action,out var threshold)||!ValidPurchaseThreshold(threshold))
        {await _sender.AnswerCallbackAsync(callback.Id,"گزینهٔ گزارش معتبر نیست.",ct,true);return true;}
        await _sender.AnswerCallbackAsync(callback.Id,"در حال آماده‌سازی گزارش…",ct);
        // A repeated click/update must not create concurrent copies of a long report.
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"perfume-purchase-report:"+actor},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Purchase report busy',1;",ct);
        if(await _db.NotificationOutbox.AnyAsync(n=>!n.IsDeleted&&n.Channel==PerfumePurchaseReportChannel&&n.Recipient==actor.ToString()&&
            (n.Status==NotificationOutboxStatus.Pending||n.Status==NotificationOutboxStatus.Processing),ct))
        {await tx.CommitAsync(ct);await _sender.SendAsync(actor.ToString(),"گزارش قبلی هنوز در حال ارسال است؛ تا پایان آن صبر کنید.",ct);return true;}
        var lists=await _db.SalesLists.AsNoTracking().Where(l=>!l.IsDeleted&&!l.IsInventoryOffer&&l.Status==SalesListStatus.Open&&
            l.TotalVolume>0&&l.ReservedVolume<l.TotalVolume&&(l.TotalVolume-l.ReservedVolume)*100<l.TotalVolume*threshold)
            .ToArrayAsync(ct);
        var ids=lists.Where(l=>PurchaseReportEligible(l,threshold))
            .OrderBy(l=>l.RemainingVolume/l.TotalVolume).ThenBy(l=>l.DisplayCode).Select(l=>l.Id).ToArray();
        _db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=Guid.Empty,Channel=PerfumePurchaseReportChannel,
            EventType="ShowPerfumePurchaseReport",Recipient=actor.ToString(),CreatedAt=DateTime.UtcNow,
            Payload=JsonSerializer.Serialize(new PurchaseReport(actor,callback.Message.MessageId,threshold,ids))});
        await _db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        await _sender.SendAsync(actor.ToString(),$"🧴 گزارش زیر {threshold}٪ باقی‌مانده\nتعداد لیست‌ها: {ids.Length}\nهمهٔ نتایج به‌ترتیب کمترین درصد باقی‌مانده ارسال می‌شوند.",ct);
        return true;
    }

    internal async Task<int?> ProcessPerfumePurchaseReportAsync(NotificationOutbox job,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==job.Id&&!n.IsDeleted&&n.Channel==PerfumePurchaseReportChannel,ct);
        var data=JsonSerializer.Deserialize<PurchaseReport>(row.Payload)!;
        if(!IsPrimaryOwner(data.Actor)||row.Recipient!=data.Actor.ToString()||job.Recipient!=row.Recipient||data.Source<=0||!ValidPurchaseThreshold(data.Threshold))
            return null;
        if(data.Finished)return null;
        if(data.Next>=data.Lists.Length)
        {
            if(data.Attempted)
            {await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Finished=true},ct);return null;}
            await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Attempted=true},ct);
            var summary=await _sender.SendReplyOnceAsync(row.Recipient,$"✅ پایان گزارش زیر {data.Threshold}٪\nنمایش داده‌شده: {data.Next-data.Failed-data.Skipped}\nلیست‌های تکمیل‌شده یا تغییرکرده در حین گزارش: {data.Skipped}\nارسال ناموفق یا تأییدنشده: {data.Failed}\nبدون عکس ثبت‌شده یا قابل ارسال: {data.WithoutPhoto}",data.Source,ct);
            if(summary.IsRetryable)
            {await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Attempted=false},ct);return Math.Max(3,summary.RetryAfterSeconds??60);}
            await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Finished=true,Attempted=false},ct);
            return null;
        }
        if(data.Attempted)
        {
            await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Next=d.Next+1,Attempted=false,Failed=d.Failed+1},ct);
            return 3; // Never duplicate an accepted message after an unknown transport outcome.
        }
        var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(l=>l.Id==data.Lists[data.Next],ct);
        if(list is null||!PurchaseReportEligible(list,data.Threshold))
        {
            await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Next=d.Next+1,Skipped=d.Skipped+1},ct);return 3;
        }
        var photo=await SalesListPhotoResolver.ResolveSourcePostAsync(_db,list,ct)??await SalesListPhotoResolver.ResolveAsync(_db,list,ct);
        var url=InventoryOffersController.PostUrl(list.TelegramChannelId,list.TelegramMessageId);
        var caption=$"🧴 {list.PersianName}\n{list.EnglishName}\nکد لیست: {list.DisplayCode}\nحجم کل: {list.TotalVolume:0.##} میل\nباقی‌مانده: {list.RemainingVolume:0.##} میل ({list.RemainingVolume/list.TotalVolume*100:0.##}٪)\n"+
            (url.Length>0?"🔗 "+url:"⚠️ لینک پست ثبت نشده است.");
        var cost=(await new PerfumePurchaseCostService(_db).CostsAsync([list],ct,DateTime.UtcNow)).Single();
        caption+="\n\n"+PurchaseCostCaption(cost);
        await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Attempted=true},ct);
        var sent=string.IsNullOrWhiteSpace(photo)
            ?await _sender.SendReplyOnceAsync(row.Recipient,caption+"\n⚠️ عکس ثبت‌شده در دسترس نیست.",data.Source,ct)
            :await _sender.SendPhotoOnceWithKeyboardAsync(row.Recipient,photo,caption,url.Length>0?[[new("🔗 مشاهدهٔ لیست",Url:url)]]:[],ct);
        if(sent.IsRetryable)
        {
            await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Attempted=false},ct);
            return Math.Max(3,sent.RetryAfterSeconds??60);
        }
        await MutateShippingPayloadAsync<PurchaseReport>(job.Id,PerfumePurchaseReportChannel,d=>d with{Next=d.Next+1,Attempted=false,
            Failed=d.Failed+(sent.IsSuccessful?0:1),WithoutPhoto=d.WithoutPhoto+(string.IsNullOrWhiteSpace(photo)?1:0)},ct);
        return 3;
    }
}
