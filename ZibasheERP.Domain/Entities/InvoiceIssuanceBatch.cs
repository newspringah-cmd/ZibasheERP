namespace ZibasheERP.Domain.Entities;

public enum InvoiceIssuanceBatchStatus
{
    Created = 1,
    Issuing = 2,
    Issued = 3,
    NeedsManualAction = 4,
    Cancelled = 5
}

/// <summary>
/// A manager-approved set of completed sales lists. One batch creates at most one
/// order/invoice per customer, even when that customer appears in several lists.
/// </summary>
public sealed class InvoiceIssuanceBatch : BaseEntity
{
    public string CreatedByTelegramUserId { get; set; } = string.Empty;
    public InvoiceIssuanceBatchStatus Status { get; set; } = InvoiceIssuanceBatchStatus.Created;
    public DateTime? IssuedAt { get; set; }
    public string? Notes { get; set; }
    public ICollection<InvoiceIssuanceBatchSalesList> SalesLists { get; set; } = new List<InvoiceIssuanceBatchSalesList>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

public sealed class InvoiceIssuanceBatchSalesList
{
    public Guid InvoiceIssuanceBatchId { get; set; }
    public InvoiceIssuanceBatch InvoiceIssuanceBatch { get; set; } = null!;
    public Guid SalesListId { get; set; }
    public SalesList SalesList { get; set; } = null!;
}
