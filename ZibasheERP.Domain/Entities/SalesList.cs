using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public enum SalesListStatus
{
    Open = 1,          // لیست باز است
    Full = 2,          // ظرفیت تکمیل شده
    Purchased = 3,     // عطر خریداری شده
    Invoiced = 4,      // فاکتور صادر شده
    Closed = 5,        // پایان کار
    Cancelled = 6      // لغو شده
}

public class SalesList : BaseEntity
{
    // بچ مربوط به این لیست
    public Guid BatchId { get; set; }

    public Batch Batch { get; set; } = null!;

    // قیمت هر میل در زمان باز شدن لیست
    public decimal PricePerMl { get; set; }

    // حجم کل شیشه اصلی
    public int TotalVolume { get; set; } = 100;

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

    // کانال تلگرام
    [MaxLength(100)]
    public string? TelegramChannelId { get; set; }

    // توضیحات
    [MaxLength(500)]
    public string? Notes { get; set; }
}
