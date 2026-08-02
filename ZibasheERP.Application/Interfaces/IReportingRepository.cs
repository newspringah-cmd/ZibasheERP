using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IReportingRepository
{
    Task<IReadOnlyCollection<Order>> GetOrdersAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Customer>> GetTopDebtorsAsync(
        int limit,
        CancellationToken cancellationToken = default);
    Task<decimal> GetTotalOutstandingDebtAsync(CancellationToken cancellationToken = default);
}
