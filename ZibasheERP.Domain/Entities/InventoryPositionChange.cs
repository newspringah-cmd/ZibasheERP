namespace ZibasheERP.Domain.Entities;

public sealed class InventoryPositionChange : BaseEntity
{
    public Guid SalesListId { get; set; }
    public string? PreviousPosition { get; set; }
    public string Position { get; set; } = string.Empty;
    public long AdminUserId { get; set; }
}
