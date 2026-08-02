using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository, IAdminCustomerRepository
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

    public async Task<Customer?> GetByMobileAsync(
        string mobile,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Customers.FirstOrDefaultAsync(
            customer => customer.Mobile == mobile && !customer.IsDeleted,
            cancellationToken);
    }

    public async Task<Customer?> GetByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var withPrefix = "@" + username;
        return await _dbContext.Customers.FirstOrDefaultAsync(
            customer =>
                (customer.Username == username || customer.Username == withPrefix) &&
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

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<Customer>> SearchAsync(
        string? search,
        bool debtOnly,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Customers.AsNoTracking()
            .Where(customer => !customer.IsDeleted);
        if (debtOnly)
            query = query.Where(customer => customer.CurrentDebt > 0);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().TrimStart('@');
            query = query.Where(customer =>
                customer.FullName.Contains(term) ||
                customer.Mobile.Contains(term) ||
                (customer.Username != null &&
                 (customer.Username.Contains(term) || customer.Username == "@" + term)) ||
                (customer.TelegramId != null && customer.TelegramId.Contains(term)));
        }

        return await query
            .OrderByDescending(customer => customer.CurrentDebt)
            .ThenBy(customer => customer.FullName)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArrayAsync(cancellationToken);
    }
}
