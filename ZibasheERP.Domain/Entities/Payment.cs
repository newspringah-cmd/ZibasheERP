namespace ZibasheERP.Domain.Entities;

public class Payment : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    public decimal Amount { get; set; }

    public string PaymentMethod { get; set; } = string.Empty;

    public string? TransactionId { get; set; }

    public bool IsSuccessful { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? Notes { get; set; }
}