using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IOrderRepository
{
    Task AddAsync(Order order);

    Task<Order?> GetByIdAsync(Guid id);

    Task SaveChangesAsync();
}