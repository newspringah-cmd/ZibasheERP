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
    Task<bool> ExistsAsync(
        string brand,
        string englishName,
        CancellationToken cancellationToken = default);
    Task AddAsync(Perfume perfume, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
