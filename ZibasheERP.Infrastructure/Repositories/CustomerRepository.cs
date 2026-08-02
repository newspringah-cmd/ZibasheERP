using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly AppDbContext _dbContext;

    public CustomerRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Customer?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Customers
            .FirstOrDefaultAsync(
                customer => customer.Id == id && !customer.IsDeleted,
                cancellationToken);
    }

    public async Task<Customer?> GetByTelegramIdAsync(
        string telegramId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Customers
            .FirstOrDefaultAsync(
                customer =>
                    customer.TelegramId == telegramId &&
                    !customer.IsDeleted,
                cancellationToken);
    }

    public async Task AddAsync(
        Customer customer,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Customers.AddAsync(customer, cancellationToken);
    }

    public Task UpdateAsync(
        Customer customer,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Customers.Update(customer);

        return Task.CompletedTask;
    }
}