using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Reports.GetBusinessReport;

public sealed class GetBusinessReportQueryHandler
    : IRequestHandler<GetBusinessReportQuery, BusinessReportResponse>
{
    private readonly IReportingRepository _repository;

    public GetBusinessReportQueryHandler(IReportingRepository repository)
    {
        _repository = repository;
    }

    public async Task<BusinessReportResponse> Handle(
        GetBusinessReportQuery request,
        CancellationToken cancellationToken)
    {
        var to = (request.To ?? DateTime.UtcNow).ToUniversalTime();
        var from = (request.From ?? to.AddDays(-30)).ToUniversalTime();
        if (from >= to)
            throw new InvalidOperationException("ابتدای بازه گزارش باید قبل از انتهای آن باشد.");
        if (to - from > TimeSpan.FromDays(366))
            throw new InvalidOperationException("بازه گزارش نمی‌تواند بیشتر از ۳۶۶ روز باشد.");

        var topLimit = Math.Clamp(request.TopLimit, 1, 100);
        var orders = await _repository.GetOrdersAsync(from, to, cancellationToken);
        var debtors = await _repository.GetTopDebtorsAsync(topLimit, cancellationToken);
        var outstandingDebt = await _repository.GetTotalOutstandingDebtAsync(cancellationToken);
        var activeOrders = orders.Where(order => order.Status != OrderState.Cancelled).ToArray();
        var payments = orders.SelectMany(order => order.Payments)
            .Where(payment => !payment.IsDeleted)
            .ToArray();

        var topPerfumes = activeOrders.SelectMany(order => order.Items)
            .Where(item => !item.IsDeleted && item.Perfume is not null)
            .GroupBy(item => new { item.PerfumeId, item.Perfume!.Name })
            .Select(group => new TopPerfumeResponse(
                group.Key.PerfumeId!.Value,
                group.Key.Name,
                group.Sum(item => item.RequestedVolumeMl),
                group.Sum(item => item.PerfumeAmount)))
            .OrderByDescending(item => item.VolumeMl)
            .ThenByDescending(item => item.Amount)
            .Take(topLimit)
            .ToArray();

        var statusSummary = orders
            .GroupBy(order => order.Status)
            .OrderBy(group => group.Key)
            .Select(group => new ReportStatusResponse(
                group.Key.ToString(),
                group.Count(),
                group.Sum(order => order.FinalAmount)))
            .ToArray();

        return new BusinessReportResponse(
            from,
            to,
            orders.Count,
            activeOrders.Length,
            orders.Count(order => order.Status == OrderState.Cancelled),
            activeOrders.Sum(order => order.FinalAmount),
            payments.Where(payment => payment.Status == PaymentStatus.Confirmed).Sum(payment => payment.Amount),
            payments.Where(payment => payment.Status == PaymentStatus.Refunded).Sum(payment => payment.Amount),
            outstandingDebt,
            activeOrders.SelectMany(order => order.Items).Where(item => !item.IsDeleted).Sum(item => item.RequestedVolumeMl),
            statusSummary,
            topPerfumes,
            debtors.Select(customer => new TopDebtorResponse(
                customer.Id,
                customer.FullName,
                customer.Username,
                customer.Mobile,
                customer.CurrentDebt,
                customer.CreditLimit)).ToArray());
    }
}
