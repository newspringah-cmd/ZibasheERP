using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IBottleRepository
{
    Task<Bottle?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}