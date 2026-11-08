namespace ZibasheERP.Domain.Entities;

/// <summary>A time-bounded perfume-only offer. The ordinary list price never changes.</summary>
public sealed class FlashSalePromotion : BaseEntity
{
    public Guid SalesListId { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public decimal OriginalPricePerMl { get; set; }
    public int DiscountPercent { get; set; }
    public decimal? DiscountAmountPerMl { get; set; }
    public int RequestedDurationMinutes { get; set; } = 120;
    public long GroupChatId { get; set; }
    public long? TelegramMessageId { get; set; }
    public DateTime? CancelledAtUtc { get; set; }

    public bool IsActiveAt(DateTime utcNow) => !IsDeleted && !CancelledAtUtc.HasValue &&
        utcNow >= StartsAtUtc && utcNow < EndsAtUtc;

    public decimal DiscountedPricePerMl => decimal.Round(
        DiscountAmountPerMl.HasValue ? OriginalPricePerMl - DiscountAmountPerMl.Value :
            OriginalPricePerMl * (100 - DiscountPercent) / 100m, 2, MidpointRounding.AwayFromZero);
}
