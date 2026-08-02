using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _dbContext;

    public OrderRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Orders.AddAsync(order, cancellationToken);
    }

    public async Task<Order?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Items)
                .ThenInclude(x => x.Perfume)
            .Include(x => x.Items)
                .ThenInclude(x => x.Bottle)
            .Include(x => x.Payments)
            .Include(x => x.Shipments)
            .FirstOrDefaultAsync(
                x => x.Id == id && !x.IsDeleted,
                cancellationToken);
    }

    public async Task<Order?> GetByOrderNumberAsync(
        string orderNumber,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Items)
                .ThenInclude(x => x.Perfume)
            .Include(x => x.Items)
                .ThenInclude(x => x.Bottle)
            .Include(x => x.Payments)
            .Include(x => x.Shipments)
            .FirstOrDefaultAsync(
                x => x.OrderNumber == orderNumber && !x.IsDeleted,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Orders
            .AsNoTracking()
            .Where(order =>
                order.CustomerId == customerId &&
                !order.IsDeleted)
            .Include(order => order.Items)
            .OrderByDescending(order => order.RegisteredAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> OrderNumberExistsAsync(
        string orderNumber,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Orders
            .AnyAsync(
                x => x.OrderNumber == orderNumber && !x.IsDeleted,
                cancellationToken);
    }

    public Task UpdateAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Orders.Update(order);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
