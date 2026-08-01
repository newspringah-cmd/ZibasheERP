using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Domain.Entities;

public class Order : BaseEntity
{
    public string OrderNumber { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }

    public Guid SalesListId { get; set; }

    public decimal VolumeMl { get; set; }

    public decimal PricePerMl { get; set; }

    public decimal TotalPrice { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public string? Notes { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public Guid? CancelledByUserId { get; set; }

    // Navigation Properties
    public Customer Customer { get; set; } = null!;

    public SalesList SalesList { get; set; } = null!;
    public Invoice? Invoice { get; set; }

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public Shipment? Shipment { get; set; }
}