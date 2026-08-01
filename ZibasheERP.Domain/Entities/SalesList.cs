namespace ZibasheERP.Domain.Entities;

public enum SalesListStatus
{
    Open = 1,        // لیست با اولین درخواست باز شده است
    Full = 2,        // حجم لیست کامل شده است
    Purchased = 3,   // عطر خریداری شده است
    Invoiced = 4,    // فاکتورها صادر شده‌اند
    Closed = 5,      // کار لیست تمام شده است
    Cancelled = 6    // لیست لغو شده است
}

public class SalesList : BaseEntity
{
    // بچ یا نوبت فروش مربوط به این لیست
    public Guid BatchId { get; set; }

    public Batch Batch { get; set; } = null!;

    // قیمت هر میل در زمان باز شدن لیست
    public decimal PricePerMl { get; set; }

    // حجم کل شیشه اصلی، مثلاً 100 میل
    public int TotalVolume { get; set; } = 100;

    // مجموع حجم درخواست‌شده مشتریان
    public int ReservedVolume { get; set; }

    // حجم باقی‌مانده لیست
    public int RemainingVolume => Math.Max(0, TotalVolume - ReservedVolume);

    // تاریخ باز شدن لیست با اولین درخواست
    public DateTime OpenDate { get; set; } = DateTime.UtcNow;

    // تاریخ تکمیل یا بسته شدن لیست
    public DateTime? ClosedDate { get; set; }

    public SalesListStatus Status { get; set; } = SalesListStatus.Open;

    // شناسه پست مربوطه در کانال تلگرام
    public long? TelegramMessageId { get; set; }

    // نام کاربری یا شناسه کانال؛ در صورت نیاز
    public string? TelegramChannelId { get; set; }

    public string? Notes { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}