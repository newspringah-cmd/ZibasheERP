namespace ZibasheERP.Domain.Entities;

public class Shipment : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    public Guid AddressId { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string ReceiverName { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string Mobile { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string Province { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(1000)]
    public string FullAddress { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string ShippingCompany { get; set; } = string.Empty;

    // هزینه ارسال فقط هنگام درخواست ارسال تعیین می‌شود
    public decimal ShippingCost { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string? TrackingCode { get; set; }

    public DateTime? RequestedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public bool IsDelivered { get; set; }

    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    public string? Notes { get; set; }
}
