using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Domain.Entities;

public class Payment : BaseEntity
{
    public Guid OrderId { get; set; }

    public decimal Amount { get; set; }

    public DateTime? PaidAt { get; set; }

    public string Method { get; set; } = string.Empty;

    public string? TransactionId { get; set; }

    public string? ReceiptImagePath { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public Guid? ConfirmedByUserId { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string? Notes { get; set; }

    public Order Order { get; set; } = null!;
}