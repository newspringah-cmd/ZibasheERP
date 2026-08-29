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

public interface IInvoiceIssuanceService
{
    Task<IReadOnlyCollection<CompletedSalesListForInvoice>> GetCompletedListsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<InvoiceIssuanceResult> IssueCompletedListsAsync(
        IReadOnlyCollection<Guid> salesListIds,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default);

    Task<InvoiceIssuanceResult> IssueManualAsync(
        string customerIdentity,
        IReadOnlyCollection<ManualInvoiceLineInput> lines,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default);
}
