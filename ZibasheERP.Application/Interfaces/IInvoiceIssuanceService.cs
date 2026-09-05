namespace ZibasheERP.Application.Interfaces;

public sealed record CompletedSalesListForInvoice(
    Guid SalesListId,
    int PublicCode,
    string PerfumeName,
    int ConfirmedRequestCount,
    int TotalVolume);

public sealed record ManualInvoiceLineInput(
    string Description,
    int Quantity,
    decimal UnitAmount,
    decimal BottleAmount = 0);

public sealed record InvoiceIssuanceResult(
    Guid BatchId,
    int InvoiceCount,
    IReadOnlyCollection<string> InvoiceNumbers,
    IReadOnlyCollection<SalesListProductionCopy> ProductionCopies);

public sealed record SalesListProductionCopy(
    Guid SalesListId,
    int PublicCode,
    string PerfumeName,
    string DecantMessage,
    string LabelPrintMessage);

public sealed record InvoicePaymentTrackingReport(
    Guid BatchId,
    Guid SalesListId,
    string Message,
    string? TelegramChatId,
    long? TelegramMessageId,
    IReadOnlyCollection<InvoicePaymentTrackingAction> Actions);

public sealed record InvoicePaymentTrackingAction(Guid OrderItemId, string Label);

public interface IInvoiceIssuanceService
{
    Task<IReadOnlyCollection<CompletedSalesListForInvoice>> GetCompletedListsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<CompletedSalesListForInvoice>> GetWaitingListsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task MoveCompletedListToWaitingAsync(
        Guid salesListId,
        CancellationToken cancellationToken = default);

    Task RestoreWaitingListAsync(
        Guid salesListId,
        CancellationToken cancellationToken = default);

    Task CancelCompletedListAsync(
        Guid salesListId,
        CancellationToken cancellationToken = default);

    Task<InvoiceIssuanceResult> IssueCompletedListsAsync(
        IReadOnlyCollection<Guid> salesListIds,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default);

    Task<InvoiceIssuanceResult> IssueManualAsync(
        string customerIdentity,
        IReadOnlyCollection<ManualInvoiceLineInput> lines,
        string productPhotoFileId,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<InvoicePaymentTrackingReport>> GetPaymentTrackingReportsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task SetPaymentTrackingMessageAsync(
        Guid batchId,
        Guid salesListId,
        string chatId,
        long messageId,
        CancellationToken cancellationToken = default);
}
