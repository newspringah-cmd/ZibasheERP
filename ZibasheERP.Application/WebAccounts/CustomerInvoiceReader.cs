using System.Globalization;
using ZibasheERP.Application.WebIdentity;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.WebAccounts;

// Internal snapshots, never HTTP response models. Adapter must load a consistent, complete view for the customer.
public sealed record InvoicePaymentReadSource(Guid Id, decimal Amount, PaymentStatus Status, bool IsDeleted = false);
public sealed record InvoiceLineReadSource(string Name, decimal VolumeMl, decimal PerfumeAmount, decimal BottleAmount, string Bottle);
public sealed record CustomerInvoiceReadSource(Guid InvoiceId, Guid OrderId, Guid CurrentCustomerId,
    string Number, DateTimeOffset IssuedAt, InvoiceStatus Status, OrderStatus OrderStatus, bool IsInventory,
    decimal TotalAmount, IReadOnlyList<InvoicePaymentReadSource> Payments, IReadOnlyList<InvoiceLineReadSource> Lines,
    bool IsDeleted = false, bool IsOrderDeleted = false);
public sealed record CustomerInvoiceReadSnapshot(DateTimeOffset AsOf, bool IsComplete,
    IReadOnlyList<CustomerInvoiceReadSource> Invoices);

public interface ICustomerInvoiceReadStore
{
    // Must use current billing ownership, not username, old owner, gift recipient or request-body CustomerId.
    // No writes. A future DB adapter must guarantee consistent payment/ownership/line snapshots and completeness.
    Task<CustomerInvoiceReadSnapshot> ReadAsync(Guid authenticatedCustomerId, CancellationToken ct);
}

// Financial decimal strings have no grouping; clients must not coerce them to floating-point numbers.
public sealed record WebInvoiceLine(string Name, string VolumeMl, string PerfumeAmount, string BottleAmount, string Bottle);
public sealed record WebCustomerInvoice(Guid Id, string Number, DateTimeOffset IssuedAt, string Status, string Source,
    string Total, string ConfirmedPayments, string Outstanding, IReadOnlyList<WebInvoiceLine> Lines);
public sealed record WebInvoiceFinanceSummary(int PaidCount, string PaidInvoiceTotal, int PendingCount,
    string PendingInvoiceTotal, string ConfirmedPartialPayments, string Outstanding);
public sealed record WebCustomerInvoices(int SchemaVersion, string Currency, DateTimeOffset AsOf,
    WebInvoiceFinanceSummary Summary, int Page, int PageSize, int TotalCount, bool HasMore,
    IReadOnlyList<WebCustomerInvoice> Invoices);

/// <summary>
/// Read-only application seam, not registered in the live API. Actor must be reconstructed from a validated
/// session and current identity directory. No endpoint, cookie, DB adapter or claim binding is provided here.
/// </summary>
public sealed class CustomerInvoiceReader(ICustomerInvoiceReadStore store)
{
    public async Task<WebCustomerInvoices> ReadMyInvoicesAsync(CurrentWebActor? actor, int page = 1,
        int pageSize = 20, CancellationToken ct = default)
    {
        var customerId = actor?.CustomerId ?? Guid.Empty;
        if (!WebReadAccess.CanReadCustomerAccount(actor, customerId))
            throw new UnauthorizedAccessException("An active authenticated customer is required.");
        if (page < 1 || pageSize is < 1 or > 50 || (long)(page - 1) * pageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(page), "Invalid invoice page.");

        var snapshot = await store.ReadAsync(customerId, ct);
        if (!snapshot.IsComplete) throw InvalidSnapshot();
        var source = snapshot.Invoices.ToArray();
        // Reject a broken tenant boundary instead of silently filtering it into an incomplete financial report.
        if (source.Any(i => i.CurrentCustomerId != customerId)) throw InvalidSnapshot();
        var active = source.Where(i => !i.IsDeleted && !i.IsOrderDeleted && i.OrderStatus != OrderStatus.Cancelled &&
            i.Status is InvoiceStatus.Issued or InvoiceStatus.Paid).ToArray();
        if (active.Any(i => i.InvoiceId == Guid.Empty || i.OrderId == Guid.Empty) ||
            active.GroupBy(i => i.InvoiceId).Any(g => g.Count() > 1) ||
            active.GroupBy(i => i.OrderId).Any(g => g.Count() > 1) ||
            active.SelectMany(i => i.Payments).Where(p => !p.IsDeleted).GroupBy(p => p.Id).Any(g => g.Count() > 1))
            throw InvalidSnapshot();

        var projected = active.Select(Project).OrderByDescending(i => i.IssuedAt).ThenBy(i => i.Id).ToArray();
        var paid = active.Where(i => i.Status == InvoiceStatus.Paid).ToArray();
        var pending = active.Where(i => i.Status == InvoiceStatus.Issued).ToArray();
        var summary = new WebInvoiceFinanceSummary(paid.Length, Decimal(paid.Sum(i => i.TotalAmount)),
            pending.Length, Decimal(pending.Sum(i => i.TotalAmount)),
            Decimal(pending.Sum(Confirmed)), Decimal(pending.Sum(i => Math.Max(0m, i.TotalAmount - Confirmed(i)))));
        var offset = (page - 1) * pageSize;
        var items = projected.Skip(offset).Take(pageSize).ToArray();
        return new(1, "IRT", snapshot.AsOf, summary, page, pageSize, projected.Length,
            (long)offset + items.Length < projected.Length, items);
    }

    private static WebCustomerInvoice Project(CustomerInvoiceReadSource invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.Number) || invoice.TotalAmount < 0 || invoice.Lines.Count == 0 ||
            invoice.Lines.Any(l => string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.Bottle) ||
                l.VolumeMl <= 0 || l.PerfumeAmount < 0 || l.BottleAmount < 0) ||
            invoice.Lines.Sum(l => l.PerfumeAmount + l.BottleAmount) != invoice.TotalAmount)
            throw InvalidSnapshot();
        var payments = invoice.Payments.Where(p => !p.IsDeleted).ToArray();
        if (payments.Any(p => p.Id == Guid.Empty) || payments.GroupBy(p => p.Id).Any(g => g.Count() > 1) ||
            payments.Any(p => p.Status == PaymentStatus.Confirmed && p.Amount < 0)) throw InvalidSnapshot();
        var confirmed = Confirmed(invoice);
        // Paid invoice status is authoritative even when imported legacy payment rows are absent.
        var outstanding = invoice.Status == InvoiceStatus.Paid ? 0 : Math.Max(0, invoice.TotalAmount - confirmed);
        return new(invoice.InvoiceId, invoice.Number, invoice.IssuedAt, invoice.Status == InvoiceStatus.Paid ? "paid" : "pending",
            invoice.IsInventory ? "inventory" : "sales-list", Decimal(invoice.TotalAmount), Decimal(confirmed), Decimal(outstanding),
            invoice.Lines.Select(l => new WebInvoiceLine(l.Name, Decimal(l.VolumeMl), Decimal(l.PerfumeAmount),
                Decimal(l.BottleAmount), l.Bottle)).ToArray());
    }

    private static decimal Confirmed(CustomerInvoiceReadSource invoice) =>
        invoice.Payments.Where(p => !p.IsDeleted && p.Status == PaymentStatus.Confirmed).Sum(p => p.Amount);
    private static string Decimal(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
    private static InvalidOperationException InvalidSnapshot() =>
        new("Invoice snapshot requires reconciliation before it can be displayed.");
}
