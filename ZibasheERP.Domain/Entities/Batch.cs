namespace ZibasheERP.Domain.Entities;

public class Batch : BaseEntity
{
    public Guid PerfumeId { get; set; }

    public decimal PurchasePrice { get; set; }

    public decimal TotalVolumeMl { get; set; }

    public decimal RemainingVolumeMl { get; set; }

    public DateTime PurchaseDate { get; set; }

    public string BatchNumber { get; set; } = string.Empty;

    public string Status { get; set; } = "Draft";

    public Perfume Perfume { get; set; } = null!;
}