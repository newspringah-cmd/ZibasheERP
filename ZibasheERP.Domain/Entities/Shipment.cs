using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Domain.Entities;

public class Shipment : BaseEntity
{
    public Guid OrderId { get; set; }

    public string Carrier { get; set; } = string.Empty;

    public string TrackingCode { get; set; } = string.Empty;

    public DateTime? ShippedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Preparing;

    public string? Notes { get; set; }

    public Order Order { get; set; } = null!;
}