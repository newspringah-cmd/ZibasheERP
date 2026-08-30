namespace ZibasheERP.Application.Interfaces;

public interface IInvoiceTelegramSettingRepository
{
    Task<string?> GetGreetingStickerFileIdAsync(CancellationToken cancellationToken = default);
    Task SetGreetingStickerFileIdAsync(
        string? fileId,
        long updatedByTelegramUserId,
        CancellationToken cancellationToken = default);
}
