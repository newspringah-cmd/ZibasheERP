using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IBatchRepository
{
    Task<Batch?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}