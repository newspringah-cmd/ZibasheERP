namespace ZibasheERP.Domain.Entities;

public sealed class InvoiceTelegramSetting : BaseEntity
{
    public string? GreetingStickerFileId { get; set; }
    public long? UpdatedByTelegramUserId { get; set; }
}
