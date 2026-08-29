namespace ZibasheERP.Domain.Entities;

public enum SalesListRequestKind
{
    CurrentBottle = 1,
    NextBottle = 2
}

public enum SalesListRequestStatus
{
    PendingConfirmation = 1,
    Confirmed = 2,
    Cancelled = 3,
    Expired = 4,
    Promoted = 5,
    QueuedForInvoice = 6,
    Invoiced = 7
}

/// <summary>
/// A channel reservation is intentionally separate from an ERP order. New Telegram
/// users can reserve perfume without a mobile number or an established credit line;
/// the accounting workflow can create the financial order later.
/// </summary>
public sealed class SalesListRequest : BaseEntity
{
    public Guid SalesListId { get; set; }
    public SalesList SalesList { get; set; } = null!;
    public string TelegramUserId { get; set; } = string.Empty;
    public string? TelegramUsername { get; set; }
    public bool IsGift { get; set; }
    public string? GiftRecipientTelegramUserId { get; set; }
    public string? GiftRecipientTelegramUsername { get; set; }
    public bool IsBottleOwner { get; set; }
    public int VolumeMl { get; set; }
    public Guid? BottleId { get; set; }
    public Bottle? Bottle { get; set; }
    public decimal PerfumePricePerMl { get; set; }
    public decimal BottlePrice { get; set; }
    public SalesListRequestKind Kind { get; set; } = SalesListRequestKind.CurrentBottle;
    public SalesListRequestStatus Status { get; set; } = SalesListRequestStatus.PendingConfirmation;
    public bool CreatedByAdmin { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
