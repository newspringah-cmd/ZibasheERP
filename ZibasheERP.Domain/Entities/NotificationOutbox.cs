namespace ZibasheERP.Domain.Entities;

public enum NotificationOutboxStatus
{
    Pending = 1,
    Processed = 2,
    Failed = 3
}

public sealed class NotificationOutbox : BaseEntity
{
    public Guid CustomerId { get; set; }
    public Guid? OrderId { get; set; }
    public string Channel { get; set; } = "Telegram";
    public string EventType { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public NotificationOutboxStatus Status { get; set; } = NotificationOutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
