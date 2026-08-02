using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IOrderArtifactRepository
{
    Task<OrderArtifact?> GetBySourceEventIdAsync(
        Guid sourceEventId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<OrderArtifact>> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);
    Task AddAsync(OrderArtifact artifact, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
