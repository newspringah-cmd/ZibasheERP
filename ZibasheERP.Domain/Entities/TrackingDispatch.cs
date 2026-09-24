using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public enum TrackingCarrier
{
    IranPost = 1,
    Chapar = 2,
    IranPostExpress = 3
}

public enum TrackingDispatchStatus
{
    NeedsReview = 1,
    Ready = 2,
    Sending = 3,
    Sent = 4,
    Failed = 5
}

public sealed class TrackingImportBatch : BaseEntity
{
    [MaxLength(64)]
    public string SourceHash { get; set; } = string.Empty;

    public TrackingCarrier Carrier { get; set; }

    public long RequestedByTelegramUserId { get; set; }

    public long SourceChatId { get; set; }

    public long SourceMessageId { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public ICollection<TrackingDispatch> Dispatches { get; set; } = new List<TrackingDispatch>();
}

public sealed class TrackingDispatch : BaseEntity
{
    public Guid ImportBatchId { get; set; }
    public TrackingImportBatch? ImportBatch { get; set; }

    public TrackingCarrier Carrier { get; set; }

    [MaxLength(100)]
    public string TrackingCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string RecipientName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Destination { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? TrackingUrl { get; set; }

    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid? ShippingRequestId { get; set; }

    public TrackingDispatchStatus Status { get; set; }

    [MaxLength(500)]
    public string? MatchNotes { get; set; }

    public byte[]? CardImage { get; set; }

    public DateTime? SendingStartedAt { get; set; }
    public DateTime? SentAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public ICollection<TrackingDispatchDelivery> Deliveries { get; set; } = new List<TrackingDispatchDelivery>();
}

public sealed class TrackingDispatchDelivery : BaseEntity
{
    public Guid TrackingDispatchId { get; set; }
    public TrackingDispatch? TrackingDispatch { get; set; }

    [MaxLength(50)]
    public string TelegramChatId { get; set; } = string.Empty;

    public DateTime? AttemptedAt { get; set; }
    public DateTime? SentAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }
}
