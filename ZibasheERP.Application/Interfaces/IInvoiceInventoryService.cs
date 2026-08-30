namespace ZibasheERP.Application.Interfaces;

public sealed record InvoiceInventoryPreview(
    Guid OrderItemId,
    string CustomerIdentity,
    string PerfumeName,
    int VolumeMl,
    string BottleName,
    decimal BottlePrice,
    decimal CurrentAmount);

public sealed record InvoiceInventoryReleaseResult(
    Guid SalesListId,
    int PublicCode,
    string PerfumeName,
    int VolumeMl,
    string BottleName,
    decimal TotalAmount,
    string PhotoFileId,
    Guid InvoiceIssuanceBatchId);

public interface IInvoiceInventoryService
{
    Task<InvoiceInventoryPreview> GetPreviewAsync(Guid orderItemId, CancellationToken cancellationToken = default);

    Task<InvoiceInventoryReleaseResult> ReleaseAsync(
        Guid orderItemId,
        decimal newTotalAmount,
        long adminTelegramUserId,
        CancellationToken cancellationToken = default);
}
