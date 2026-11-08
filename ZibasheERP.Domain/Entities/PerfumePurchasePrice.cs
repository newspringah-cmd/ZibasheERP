namespace ZibasheERP.Domain.Entities;

public sealed class PerfumePurchasePrice : BaseEntity
{
    public Guid SalesListId { get; set; }
    public string VariantKey { get; set; } = string.Empty;
    public decimal AmountAed { get; set; }
    public decimal BottleVolumeMl { get; set; }
    public long RecordedByUserId { get; set; }
}
