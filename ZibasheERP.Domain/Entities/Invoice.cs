using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Domain.Entities;

public class Invoice : BaseEntity
{
    public string InvoiceNumber { get; set; } = string.Empty;

    public Guid OrderId { get; set; }

    public decimal Amount { get; set; }

    public DateTime IssuedAt { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public string? Notes { get; set; }

    // Navigation Property
    public Order Order { get; set; } = null!;

}