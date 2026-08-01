namespace ZibasheERP.Domain.Entities;

public enum BottleType
{
    Normal = 1,
    Fancy = 2
}

public class Bottle : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    public int VolumeMl { get; set; }

    public BottleType Type { get; set; }

    public decimal SalePrice { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }
}