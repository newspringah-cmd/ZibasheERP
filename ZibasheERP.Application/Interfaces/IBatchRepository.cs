using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IBatchRepository
{
    Task<IReadOnlyCollection<Batch>> GetForInventoryAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<Batch?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<bool> BatchNumberExistsAsync(
        string batchNumber,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Batch batch,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Batch batch,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(
        CancellationToken cancellationToken = default);
}
