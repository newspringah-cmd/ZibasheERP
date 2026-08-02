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

    Task<IReadOnlyCollection<Bottle>> GetForAdminAsync(
        bool includeInactive,
        int limit,
        CancellationToken cancellationToken = default);

    Task<Bottle?> GetForAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string name, int volumeMl, BottleType type, CancellationToken cancellationToken = default);
    Task<bool> DefaultExistsAsync(int volumeMl, BottleType type, CancellationToken cancellationToken = default);
    Task AddAsync(Bottle bottle, CancellationToken cancellationToken = default);
    Task UpdateAsync(Bottle bottle, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
