using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;
public static class FlashSaleMainPriceDisplay
{
    public const string Channel="FlashSaleMainPriceDisplay";
    public sealed record State(Guid ListId,Guid? PromotionId,decimal? Price,bool Visible);
    public static Guid Key(Guid list)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("flash-main-price:"+list.ToString("N")))[..16]);
    public static async Task<Guid[]> TargetsAsync(AppDbContext db,DateTime now,CancellationToken ct)
    {
        var active=await (from p in db.FlashSalePromotions.AsNoTracking()
            join origin in db.SalesLists.AsNoTracking() on p.SalesListId equals origin.Id
            from list in db.SalesLists.AsNoTracking()
            where !p.IsDeleted&&p.CancelledAtUtc==null&&p.StartsAtUtc<=now&&p.EndsAtUtc>now&&
                !list.IsDeleted&&!list.IsInventoryOffer&&list.TelegramMessageId!=null&&list.TelegramChannelId!=null&&
                (list.Status==SalesListStatus.Open||list.Status==SalesListStatus.Full)&&
                (p.SalesListId==list.Id||(!origin.IsInventoryOffer&&origin.PerfumeId==list.PerfumeId&&
                 (origin.StablePublicCode??origin.PublicCode)==(list.StablePublicCode??list.PublicCode)&&list.OpenDate>=p.StartsAtUtc&&list.OpenDate<p.EndsAtUtc))
            select list.Id).Distinct().ToArrayAsync(ct);
        var old=await db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==Channel&&
            (n.Payload.Contains("\"Visible\":true")||n.Status==NotificationOutboxStatus.Pending)).Select(n=>n.Payload).ToArrayAsync(ct);
        var clean=new List<Guid>();
        foreach(var payload in old){try{var state=JsonSerializer.Deserialize<State>(payload);if(state is not null)clean.Add(state.ListId);}catch(JsonException){}}
        return active.Concat(clean).Distinct().ToArray();
    }
    public static State Snapshot(SalesList list,FlashSalePricing.Quote quote)=>new(list.Id,quote.PromotionId,quote.PromotionId.HasValue?quote.PricePerMl:null,quote.PromotionId.HasValue);
}
