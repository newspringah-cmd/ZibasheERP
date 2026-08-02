using MediatR;

namespace ZibasheERP.Application.Features.Reports.GetBusinessReport;

public sealed record GetBusinessReportQuery(
    DateTime? From = null,
    DateTime? To = null,
    int TopLimit = 10) : IRequest<BusinessReportResponse>;

public sealed record BusinessReportResponse(
    DateTime From,
    DateTime To,
    int TotalOrders,
    int ActiveOrders,
    int CancelledOrders,
    decimal GrossOrderAmount,
    decimal ConfirmedPaymentAmount,
    decimal RefundedPaymentAmount,
    decimal OutstandingDebt,
    int SoldVolumeMl,
    IReadOnlyCollection<ReportStatusResponse> OrdersByStatus,
    IReadOnlyCollection<TopPerfumeResponse> TopPerfumes,
    IReadOnlyCollection<TopDebtorResponse> TopDebtors);

public sealed record ReportStatusResponse(string Status, int Count, decimal Amount);
public sealed record TopPerfumeResponse(Guid PerfumeId, string Name, int VolumeMl, decimal Amount);
public sealed record TopDebtorResponse(
    Guid CustomerId,
    string FullName,
    string? Username,
    string Mobile,
    decimal CurrentDebt,
    decimal CreditLimit);
