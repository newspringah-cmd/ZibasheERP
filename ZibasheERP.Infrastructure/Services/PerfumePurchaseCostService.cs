using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed record PerfumePurchaseCost(Guid SalesListId,decimal? AmountAed,decimal? Rate,decimal? CostToman,
    DateTime? CompletedAt,DateTime? RateSince,string Missing,bool Estimate=false);

public sealed class PerfumePurchaseCostService(AppDbContext db)
{
    public static string VariantKey(SalesList list)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new{list.PerfumeId,list.EnglishName,list.DisplayBrand,list.ProductPageUrl,Volume=list.TotalVolume.ToString("0.0",CultureInfo.InvariantCulture)}))));

    public async Task RegisterRateAsync(Guid operation,decimal amount,long actor,CancellationToken ct)
    {
        ValidateAmount(amount);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await Lock(ct);
        var existing=await db.DirhamExchangeRates.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==operation,ct);
        if(existing is not null)
        {if(existing.RecordedByUserId!=actor||existing.TomanPerDirham!=amount)throw new InvalidOperationException("تأیید نرخ معتبر نیست.");await tx.CommitAsync(ct);return;}
        var now=DateTime.UtcNow;var last=await db.DirhamExchangeRates.OrderByDescending(r=>r.EffectiveAtUtc).Select(r=>(DateTime?)r.EffectiveAtUtc).FirstOrDefaultAsync(ct);
        if(last>=now)throw new InvalidOperationException("زمان ثبت نرخ قابل تأیید نیست؛ دوباره تلاش کنید.");
        db.DirhamExchangeRates.Add(new(){Id=operation,TomanPerDirham=amount,RecordedByUserId=actor,EffectiveAtUtc=now,CreatedAt=now});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }

    public async Task RegisterPriceAsync(Guid operation,Guid listId,string expectedVariant,decimal amount,long actor,CancellationToken ct)
    {
        ValidateAmount(amount);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);
        var list=await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==listId&&!l.IsDeleted&&!l.IsInventoryOffer,ct);
        if(list.TotalVolume<=0||VariantKey(list)!=expectedVariant)throw new InvalidOperationException("اطلاعات عطر تغییر کرده؛ دوباره جستجو کنید.");
        var existing=await db.PerfumePurchasePrices.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==operation,ct);
        if(existing is not null)
        {if(existing.RecordedByUserId!=actor||existing.SalesListId!=listId||existing.AmountAed!=amount)throw new InvalidOperationException("تأیید قیمت معتبر نیست.");await tx.CommitAsync(ct);return;}
        db.PerfumePurchasePrices.Add(new(){Id=operation,SalesListId=list.Id,VariantKey=expectedVariant,AmountAed=amount,
            BottleVolumeMl=list.TotalVolume,RecordedByUserId=actor,CreatedAt=DateTime.UtcNow});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }

    private Task<int> Lock(CancellationToken ct)=>db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"dirham-purchase-ledger"},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Dirham ledger busy',1;",ct);
    private static void ValidateAmount(decimal amount)
    {if(amount<=0||amount>1000000000m||decimal.Round(amount,4)!=amount)throw new InvalidOperationException("مبلغ مثبت با حداکثر ۴ رقم اعشار وارد کنید (حداکثر یک میلیارد).");}

    public static PerfumePurchaseCost Calculate(SalesList list,IReadOnlyCollection<SalesListRequest> requests,
        IEnumerable<PerfumePurchasePrice> prices,IEnumerable<DirhamExchangeRate> rates,DateTime? estimateAt=null)
    {
        var completed=SalesListCompletionDate.CompletedAtUtc(list,requests);var at=completed??estimateAt;
        if(list.IsInventoryOffer||list.IsDeleted)return new(list.Id,null,null,null,completed,null,"مربوط به لیست عطر نیست");
        var key=VariantKey(list);
        // Explicit prices entered/corrected for this cycle take precedence, including historical data entry.
        // A later price on a different cycle cannot rewrite the purchase price of an earlier completed cycle.
        var matching=prices.Where(p=>!p.IsDeleted&&p.VariantKey==key&&p.BottleVolumeMl==list.TotalVolume);
        var price=matching.Where(p=>p.SalesListId==list.Id).OrderByDescending(p=>p.CreatedAt).ThenByDescending(p=>p.Id).FirstOrDefault()??
            (at.HasValue?matching.Where(p=>p.CreatedAt<=at.Value).OrderByDescending(p=>p.CreatedAt).ThenByDescending(p=>p.Id).FirstOrDefault():null);
        if(!at.HasValue)return new(list.Id,price?.AmountAed,null,null,null,null,"زمان تکمیل ثبت نشده");
        var rate=rates.Where(r=>!r.IsDeleted&&r.EffectiveAtUtc<=at.Value).OrderByDescending(r=>r.EffectiveAtUtc).FirstOrDefault();
        var missing=price is null?"قیمت خرید ثبت نشده":rate is null?"نرخ معتبر در زمان تکمیل ثبت نشده":"";
        return new(list.Id,price?.AmountAed,rate?.TomanPerDirham,price is not null&&rate is not null?
            decimal.Round(price.AmountAed*rate.TomanPerDirham,0,MidpointRounding.AwayFromZero):null,completed,rate?.EffectiveAtUtc,missing,!completed.HasValue&&estimateAt.HasValue);
    }

    public async Task<PerfumePurchaseCost[]> CostsAsync(SalesList[] lists,CancellationToken ct,DateTime? estimateAt=null)
    {
        var ids=lists.Select(l=>l.Id).ToArray();var keys=lists.Select(VariantKey).Distinct().ToArray();
        var requests=await db.SalesListRequests.AsNoTracking().Where(r=>ids.Contains(r.SalesListId)&&!r.IsDeleted).ToArrayAsync(ct);
        var prices=await db.PerfumePurchasePrices.AsNoTracking().Where(p=>!p.IsDeleted&&keys.Contains(p.VariantKey)).ToArrayAsync(ct);
        var rates=await db.DirhamExchangeRates.AsNoTracking().Where(r=>!r.IsDeleted).ToArrayAsync(ct);
        var lookup=requests.ToLookup(r=>r.SalesListId);
        return lists.Select(l=>Calculate(l,lookup[l.Id].ToArray(),prices,rates,estimateAt)).ToArray();
    }
}
