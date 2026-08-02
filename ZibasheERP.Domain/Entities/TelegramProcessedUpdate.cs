namespace ZibasheERP.Domain.Entities;

public sealed class TelegramProcessedUpdate
{
    public long UpdateId { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
