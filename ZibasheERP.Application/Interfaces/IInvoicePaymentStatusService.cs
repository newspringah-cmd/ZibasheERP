namespace ZibasheERP.Application.Interfaces;

public sealed record InvoicePaymentStatusResult(
    Guid InvoiceId,
    string InvoiceNumber,
    bool IsPaid,
    DateTime? PaidAt,
    Guid? InvoiceIssuanceBatchId);

public interface IInvoicePaymentStatusService
{
    Task<InvoicePaymentStatusResult> MarkPaidAsync(
        Guid invoiceId,
        long confirmedByTelegramUserId,
        CancellationToken cancellationToken = default);

    Task<InvoicePaymentStatusResult> KeepWaitingAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default);
}
