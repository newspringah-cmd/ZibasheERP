namespace ZibasheERP.Domain.Entities;

public class Shipment : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    public string ShippingCompany { get; set; } = string.Empty;

    // هزینه ارسال فقط هنگام درخواست ارسال تعیین می‌شود
    public decimal ShippingCost { get; set; }

    public string? TrackingCode { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public bool IsDelivered { get; set; }

    public string? Notes { get; set; }
}