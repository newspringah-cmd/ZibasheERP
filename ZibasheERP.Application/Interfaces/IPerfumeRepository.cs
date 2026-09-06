using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IPerfumeRepository
{
    Task<Perfume?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Perfume>> GetAllAsync(
        bool includeInactive,
        int limit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Perfume>> GetAllActiveForPriceUpdateAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Perfume>>(Array.Empty<Perfume>());
    Task<bool> ExistsAsync(
        string brand,
        string englishName,
        CancellationToken cancellationToken = default);
    Task AddAsync(Perfume perfume, CancellationToken cancellationToken = default);
    Task UpdateAsync(Perfume perfume, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
