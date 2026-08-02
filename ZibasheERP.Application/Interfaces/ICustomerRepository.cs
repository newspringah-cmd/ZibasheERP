namespace ZibasheERP.Application.Interfaces;

using ZibasheERP.Domain.Entities;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Customer?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default);

    Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default);

    Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);

    Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
