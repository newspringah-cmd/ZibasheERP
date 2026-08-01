namespace ZibasheERP.Domain.Entities;

public class Invoice : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    public string InvoiceNumber { get; set; } = string.Empty;

    public decimal PerfumeTotal { get; set; }

    public decimal BottleTotal { get; set; }

    // هزینه ارسال در فاکتور لحاظ نمی‌شود
    public decimal TotalAmount { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public bool IsSentToCustomer { get; set; }

    public DateTime? SentToCustomerAt { get; set; }
}