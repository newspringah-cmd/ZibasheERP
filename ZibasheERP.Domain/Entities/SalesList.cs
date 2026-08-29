using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public enum SalesListStatus
{
    Open = 1,          // لیست باز است
    Full = 2,          // ظرفیت تکمیل شده
    Purchased = 3,     // عطر خریداری شده
    Invoiced = 4,      // فاکتور صادر شده
    Closed = 5,        // پایان کار
    Cancelled = 6,     // لغو شده
    QueuedForInvoice = 7
}

public enum PerfumeGender
{
    Women = 1,
    Men = 2,
    Unisex = 3
}

public class SalesList : BaseEntity
{
    public int PublicCode { get; set; }

    [MaxLength(200)]
    public string EnglishName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string ProductPageUrl { get; set; } = string.Empty;

    [MaxLength(150)]
    public string DisplayBrand { get; set; } = string.Empty;

    public PerfumeGender Gender { get; set; } = PerfumeGender.Unisex;

    public int ReleaseYear { get; set; }

    [MaxLength(200)]
    public string PersianName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string TopNotes { get; set; } = string.Empty;

    [MaxLength(500)]
    public string MiddleNotes { get; set; } = string.Empty;

    [MaxLength(500)]
    public string BaseNotes { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Accords { get; set; } = string.Empty;

    // عطر موضوع لیست سفارش‌محور؛ قبل از خرید مستقل از بچ است.
    public Guid PerfumeId { get; set; }
    public Perfume Perfume { get; set; } = null!;

    // بچ واقعی پس از تکمیل لیست و خرید به آن متصل می‌شود.
    public Guid? BatchId { get; set; }

    public Batch? Batch { get; set; }

    // قیمت هر میل در زمان باز شدن لیست
    public decimal PricePerMl { get; set; }

    // حجم کل شیشه اصلی
    public int TotalVolume { get; set; } = 100;

    // Smallest volume shown to customers on the channel post.
    public int MinimumRequestVolumeMl { get; set; } = 1;

    // حجم رزرو شده
    public int ReservedVolume { get; set; }

    // حجم باقی مانده
    public int RemainingVolume => Math.Max(0, TotalVolume - ReservedVolume);

    // آیا صاحب باتل مشخص شده است؟
    public bool HasBottleOwner { get; set; }

    // مشتری صاحب باتل
    public Guid? BottleOwnerCustomerId { get; set; }

    public Customer? BottleOwnerCustomer { get; set; }

    // تاریخ باز شدن لیست
    public DateTime OpenDate { get; set; } = DateTime.UtcNow;

    // تاریخ بسته شدن
    public DateTime? ClosedDate { get; set; }

    // وضعیت لیست
    public SalesListStatus Status { get; set; } = SalesListStatus.Open;

    // Optimistic concurrency token for volume reservations and bottle ownership.
    public byte[] RowVersion { get; set; } = [];

    // پیام تلگرام
    public long? TelegramMessageId { get; set; }

    public long? TelegramDiscussionMessageId { get; set; }

    // Optional second photo post used when the main caption cannot hold the complete roster.
    public long? TelegramContinuationMessageId { get; set; }

    [MaxLength(500)]
    public string? TelegramPhotoFileId { get; set; }

    // کانال تلگرام
    [MaxLength(100)]
    public string? TelegramChannelId { get; set; }

    // توضیحات
    [MaxLength(500)]
    public string? Notes { get; set; }

    public ICollection<SalesListRequest> Requests { get; set; } = new List<SalesListRequest>();
}
