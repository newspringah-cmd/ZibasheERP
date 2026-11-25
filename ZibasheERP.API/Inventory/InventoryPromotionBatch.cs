using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Marketing;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Inventory;

public sealed class InventoryPromotionBatch(AppDbContext db)
{
    // A batch shares one announcement instead of publishing a separate promotion for every item.
    public const long ChannelPriceOnly = 0;
    public const string AnnouncementEvent = "InventoryBatchPromotionAnnouncement";
    public const int MaxAnnouncementPhotos = 9;
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
        int percent, int minutes, long administratorId, CancellationToken ct)
    {
        if (selection.Count == 0 || selection.Select(value => value.Id).Distinct().Count() != selection.Count ||
            string.IsNullOrWhiteSpace(channel) || percent is <1 or >99 || minutes is <1 or >10080 || administratorId <= 0)
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
        var announcements = new List<(SalesList List, FlashSalePromotion Offer)>();
        foreach (var entry in selection)
        {
            var offer = entry.Offer(percent);
            offer.Id = Guid.NewGuid();
            offer.CreatedAt = offer.StartsAtUtc = starts;
            offer.EndsAtUtc = ends;
            offer.RequestedDurationMinutes = minutes;
            db.FlashSalePromotions.Add(offer);
            announcements.Add((live[entry.Id], offer));
        }
        db.NotificationOutbox.AddRange(AnnouncementJobs(announcements, administratorId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ends;
    }

    public static NotificationOutbox[] AnnouncementJobs(
        IReadOnlyCollection<(SalesList List, FlashSalePromotion Offer)> entries, long administratorId,
        IEnumerable<Guid>? batchOfferIds = null)
    {
        if (entries.Count == 0) return [];
        var first = entries.First().Offer;
        var expires = DateTime.SpecifyKind(first.EndsAtUtc, DateTimeKind.Utc);
        var localEnd = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(expires, "Asia/Tehran");
        var at = HourlyPromotionCopy.Number(localEnd.Hour, "00") + ":" + HourlyPromotionCopy.Number(localEnd.Minute, "00");
        var header = $"⚡️ پروموشن دسته‌ای موجودی زیباشی\n🎁 {HourlyPromotionCopy.Number(first.DiscountPercent)}٪ تخفیف روی مبلغ عطر\n" +
            $"⏰ تا ساعت {at} روز {ShippingAddressDisplay.RegisteredDate(expires)} به وقت ایران\n\n";
        const string footer = "\n\n📦 بسته‌ها آمادهٔ ارسالن. برای سفارش، لینک بستهٔ دلخواهتون رو باز کنین و «درخواست خرید» رو بزنین. 🤍";
        var pages = new List<(string Text, InventoryPromotionImage[] Photos)>();
        var page = new StringBuilder(header);
        var photos = new List<InventoryPromotionImage>();
        foreach (var (list, offer) in entries)
        {
            var url = InventoryOffersController.PostUrl(list.TelegramChannelId, list.TelegramMessageId);
            if (url.Length == 0)
                throw new InvalidOperationException($"لینک سفارش موجودی {list.DisplayCode} معتبر نیست.");
            var name = string.Join(' ', (string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var quantity = list.IsMiscellaneousInventory ? "۱ عدد" : $"{HourlyPromotionCopy.Number(list.TotalVolume)} میل";
            var bottle = list.FixedBottlePrice ?? 0;
            var regular = HourlyPromotionCopy.Number(offer.OriginalPricePerMl * list.TotalVolume + bottle, "#,0.##");
            var sale = HourlyPromotionCopy.Number(offer.DiscountedPricePerMl * list.TotalVolume + bottle, "#,0.##");
            var row = $"🧴 {name} · {quantity} · کد {HourlyPromotionCopy.Number(list.DisplayCode)}\n" +
                $"💰 عادی {regular} ← با تخفیف {sale} تومان\n{url}";
            if (photos.Count == MaxAnnouncementPhotos || page.Length + row.Length + footer.Length + 2 > 4096)
            {
                pages.Add((page.ToString().TrimEnd() + footer, photos.ToArray()));
                page.Clear().Append(header);
                photos.Clear();
            }
            page.Append(row).Append("\n\n");
            if (string.IsNullOrWhiteSpace(list.TelegramPhotoFileId))
                throw new InvalidOperationException($"عکس موجودی {list.DisplayCode} پیدا نشد.");
            photos.Add(new(list.DisplayCode, name, list.TelegramPhotoFileId));
        }
        pages.Add((page.ToString().TrimEnd() + footer, photos.ToArray()));

        var identity = string.Join(",", (batchOfferIds ?? entries.Select(entry => entry.Offer.Id)).Order());
        var createdAt = DateTime.UtcNow;
        return pages.Select((entry, index) => new NotificationOutbox
        {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"inventory-batch-ad:{identity}:{index}"))[..16]),
            CustomerId = Guid.Empty, Channel = MarketingPromotionJob.Channel, EventType = AnnouncementEvent,
            Recipient = administratorId.ToString(), CreatedAt = createdAt.AddTicks(index),
            Payload = JsonSerializer.Serialize(new MarketingPromotionJob(Guid.Empty, 0, MarketingPromotionJob.GroupMessage,
                administratorId, administratorId, MessageText: entry.Text, PublishBeforeUtc: expires, InventoryPhotos: entry.Photos))
        }).ToArray();
    }
}
