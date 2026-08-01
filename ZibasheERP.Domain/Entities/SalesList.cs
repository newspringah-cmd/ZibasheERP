namespace ZibasheERP.Domain.Entities;

public class SalesList : BaseEntity
{
    public Guid BatchId { get; set; }

    public decimal PricePerMl { get; set; }

    public DateTime OpenDate { get; set; }

    public DateTime? CloseDate { get; set; }

    public string Status { get; set; } = "Draft";

    public Batch Batch { get; set; } = null!;
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}