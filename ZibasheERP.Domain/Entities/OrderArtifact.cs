using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public enum OrderArtifactType
{
    InvoicePdf = 1,
    DecantPhoto = 2,
    PostalReceipt = 3
}

public sealed class OrderArtifact : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public Guid SourceEventId { get; set; }
    public OrderArtifactType Type { get; set; }

    [MaxLength(2000)]
    public string? FileUrl { get; set; }

    [MaxLength(250)]
    public string? ExternalFileId { get; set; }

    [MaxLength(100)]
    public string? ContentType { get; set; }

    public DateTime DeliveredAt { get; set; }
}
