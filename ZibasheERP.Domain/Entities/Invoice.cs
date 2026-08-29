namespace ZibasheERP.Domain.Entities;

public enum InvoiceDeliveryStatus
{
    Pending = 0,
    Delivered = 1,
    RetryScheduled = 2,
    NeedsManualAction = 3,
    ManuallySent = 4,
    Failed = 5
}

public class Invoice : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    public ZibasheERP.Domain.Enums.InvoiceStatus Status { get; set; }
        = ZibasheERP.Domain.Enums.InvoiceStatus.Draft;

    public decimal PerfumeTotal { get; set; }

    public decimal BottleTotal { get; set; }

    // هزینه ارسال در فاکتور لحاظ نمی‌شود
    public decimal TotalAmount { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public bool IsSentToCustomer { get; set; }

    public DateTime? SentToCustomerAt { get; set; }

    public InvoiceDeliveryStatus DeliveryStatus { get; set; } = InvoiceDeliveryStatus.Pending;
    public DateTime? DeliveryStatusChangedAt { get; set; }
    public string? DeliveryStatusNote { get; set; }
}
