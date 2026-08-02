using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IBottleRepository
{
    Task<Bottle?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Bottle?> GetByTypeAsync(
        BottleType type,
        CancellationToken cancellationToken = default);

    Task<List<Bottle>> GetActiveAsync(
        CancellationToken cancellationToken = default);
}