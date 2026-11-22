using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Inventory;

public sealed class InventoryPromotionBatch(AppDbContext db)
{
    // No separate promotional announcement is created for each item in a batch.
    public const long ChannelPriceOnly = 0;
    public sealed record Selection(Guid Id, int Code, decimal Price, decimal Volume, decimal BottlePrice)
    {
        public decimal Total => Price * Volume + BottlePrice;
        public decimal DiscountedTotal(int percent) => Offer(percent).DiscountedPricePerMl * Volume + BottlePrice;
        public FlashSalePromotion Offer(int percent) => new()
        {
            SalesListId = Id, DiscountPercent = percent, OriginalPricePerMl = Price, GroupChatId = ChannelPriceOnly
        };
    }

    public IQueryable<SalesList> Available(string channel, DateTime now) =>
        db.SalesLists.AsNoTracking().Where(list =>
            !list.IsDeleted && list.IsInventoryOffer && list.InventoryCode != null &&
            list.Status == SalesListStatus.Open && list.TotalVolume > 0 && list.ReservedVolume == 0 &&
            list.PricePerMl > 0 && list.TelegramMessageId != null && list.TelegramChannelId == channel &&
            list.TelegramPhotoFileId != null && list.TelegramPhotoFileId != "" &&
            !db.FlashSalePromotions.Any(offer => offer.SalesListId == list.Id && !offer.IsDeleted &&
                offer.CancelledAtUtc == null && offer.EndsAtUtc > now));

    public static Selection FromList(SalesList list) =>
        new(list.Id, list.DisplayCode, list.PricePerMl, list.TotalVolume, list.FixedBottlePrice ?? 0);

    public async Task<DateTime> ActivateAsync(IReadOnlyCollection<Selection> selection, string channel,
        int percent, int minutes, CancellationToken ct)
    {
        if (selection.Count == 0 || selection.Select(value => value.Id).Distinct().Count() != selection.Count ||
            string.IsNullOrWhiteSpace(channel) || percent is <1 or >99 || minutes is <1 or >10080)
            throw new InvalidOperationException("انتخاب، درصد یا مدت پروموشن معتبر نیست.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var entry in selection.OrderBy(value => value.Id))
        {
            var item = await db.SalesLists.FromSqlInterpolated(
                    $"SELECT * FROM SalesLists WITH(UPDLOCK,HOLDLOCK) WHERE Id={entry.Id}")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (item?.InventoryReferenceListId != null)
                await InventoryReferencePricing.SynchronizeOneAsync(db, entry.Id, ct);
        }
        var starts = DateTime.UtcNow;
        var ids = selection.Select(value => value.Id).ToArray();
        var live = await Available(channel, starts).Where(list => ids.Contains(list.Id)).ToDictionaryAsync(list => list.Id, ct);
        var invalid = selection.Where(entry => !live.TryGetValue(entry.Id, out var item) || FromList(item) != entry ||
            entry.Offer(percent).DiscountedPricePerMl <= 0 ||
            entry.Offer(percent).DiscountedPricePerMl >= entry.Price).Select(entry => entry.Code).ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException("قیمت یا وضعیت این کدها تغییر کرده یا تخفیف برایشان معتبر نیست: " +
                string.Join("، ", invalid) + "\nانتخاب‌ها را دوباره بررسی کنید.");
        var ends = starts.AddMinutes(minutes);
        foreach (var entry in selection)
        {
            var offer = entry.Offer(percent);
            offer.Id = Guid.NewGuid();
            offer.CreatedAt = offer.StartsAtUtc = starts;
            offer.EndsAtUtc = ends;
            offer.RequestedDurationMinutes = minutes;
            db.FlashSalePromotions.Add(offer);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ends;
    }
}
