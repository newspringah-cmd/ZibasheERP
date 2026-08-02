using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IAdminCustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Customer>> SearchAsync(
        string? search,
        bool debtOnly,
        int limit,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
