using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id);

    Task<Customer?> GetByTelegramIdAsync(string telegramId);

    Task UpdateAsync(Customer customer);
}