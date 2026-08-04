namespace ZibasheERP.Domain.Entities;

public sealed class InvoicePaymentAccount : BaseEntity
{
    public string CardNumber { get; set; } = string.Empty;
    public string AccountHolder { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
