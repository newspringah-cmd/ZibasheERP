namespace ZibasheERP.Domain.Entities;

public enum AdminItemAction
{
    TrackingStarted = 0,
    Registered = 1,
    Removed = 2,
    VolumeChanged = 3
}

public sealed class AdminItemActivity : BaseEntity
{
    public long ActorTelegramUserId { get; set; }
    public string? ActorUsername { get; set; }
    public string ActorDisplayName { get; set; } = string.Empty;
    public long SourceUpdateId { get; set; }
    public Guid? SalesListId { get; set; }
    public Guid? SalesListRequestId { get; set; }
    public AdminItemAction Action { get; set; }
    public int? PreviousVolumeMl { get; set; }
    public int? NewVolumeMl { get; set; }
}
