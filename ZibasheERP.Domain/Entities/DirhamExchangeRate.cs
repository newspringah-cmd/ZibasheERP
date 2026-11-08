namespace ZibasheERP.Domain.Entities;

// Append-only history; a rate remains valid until the next effective timestamp.
public sealed class DirhamExchangeRate : BaseEntity
{
    public decimal TomanPerDirham { get; set; }
    public DateTime EffectiveAtUtc { get; set; }
    public long RecordedByUserId { get; set; }
}
