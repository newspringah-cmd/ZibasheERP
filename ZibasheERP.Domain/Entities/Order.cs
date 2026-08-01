namespace ZibasheERP.Domain.Entities;

public enum OrderStatus
{
    Registered = 1,        // ثبت سفارش
    ListCompleted = 2,     // لیست تکمیل شده
    PerfumePurchased = 3,  // عطر خریداری شده
    Invoiced = 4,          // فاکتور صادر شده
    Paid = 5,              // پرداخت شده
    Decanted = 6,          // دکانت انجام شده
    ReadyToShip = 7,       // آماده ارسال
    Shipped = 8,           // ارسال شده
    Cancelled = 9          // لغو توسط ادمین
}

public class Order : BaseEntity
{
    public Guid CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    // وضعیت فعلی سفارش
    public OrderStatus Status { get; set; } = OrderStatus.Registered;

    // تاریخ ثبت سفارش
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    // جمع مبلغ عطرها
    public decimal PerfumeTotal { get; set; }

    // جمع مبلغ شیشه‌ها
    public decimal BottleTotal { get; set; }

    // مبلغ نهایی (بدون هزینه ارسال)
    public decimal FinalAmount { get; set; }

    // زمان صدور فاکتور
    public DateTime? InvoiceIssuedAt { get; set; }

    // زمان پرداخت
    public DateTime? PaidAt { get; set; }

    // زمان ارسال
    public DateTime? ShippedAt { get; set; }

    // فقط ادمین می‌تواند سفارش را لغو کند
    public Guid? CancelledByUserId { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public string? Notes { get; set; }

    public ICollection<OrderItem> Items { get; set; }
        = new List<OrderItem>();

    public ICollection<Payment> Payments { get; set; }
        = new List<Payment>();

    public ICollection<Shipment> Shipments { get; set; }
        = new List<Shipment>();
}