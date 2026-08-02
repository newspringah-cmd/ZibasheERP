using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IAddressRepository
{
    Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default);
    Task AddAsync(Address address, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
