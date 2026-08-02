using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class ReportingRepository : IReportingRepository
{
    private readonly AppDbContext _dbContext;

    public ReportingRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<Order>> GetOrdersAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Orders
            .AsNoTracking()
            .Include(order => order.Items)
                .ThenInclude(item => item.Perfume)
            .Include(order => order.Payments)
            .Where(order => !order.IsDeleted &&
                            order.RegisteredAt >= from &&
                            order.RegisteredAt < to)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Customer>> GetTopDebtorsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Customers
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted && customer.CurrentDebt > 0)
            .OrderByDescending(customer => customer.CurrentDebt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArrayAsync(cancellationToken);

    public Task<decimal> GetTotalOutstandingDebtAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Customers
            .Where(customer => !customer.IsDeleted)
            .SumAsync(customer => customer.CurrentDebt, cancellationToken);
}
