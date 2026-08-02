namespace ZibasheERP.Domain.Entities;

public class Payment : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    public decimal Amount { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(50)]
    public string PaymentMethod { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string? TransactionId { get; set; }

    public ZibasheERP.Domain.Enums.PaymentStatus Status { get; set; }
        = ZibasheERP.Domain.Enums.PaymentStatus.Pending;

    // Kept for backward compatibility; Status is the source of truth.
    public bool IsSuccessful { get; set; }

    public DateTime? PaidAt { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    public string? Notes { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
