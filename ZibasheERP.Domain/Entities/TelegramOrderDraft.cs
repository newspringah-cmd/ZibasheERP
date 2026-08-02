namespace ZibasheERP.Domain.Entities;

public enum TelegramOrderDraftStatus
{
    Pending = 1,
    Completed = 2,
    Expired = 3,
    Cancelled = 4
}

public sealed class TelegramOrderDraft : BaseEntity
{
    public string TelegramId { get; set; } = string.Empty;
    public Guid SalesListId { get; set; }
    public int VolumeMl { get; set; }
    public Guid BottleId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public TelegramOrderDraftStatus Status { get; set; } = TelegramOrderDraftStatus.Pending;
    public Guid? OrderId { get; set; }
}
