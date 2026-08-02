using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IBatchRepository
{
    Task<Batch?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Batch batch,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(
        CancellationToken cancellationToken = default);
}