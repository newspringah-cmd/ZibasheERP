using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class OrderArtifactRepository : IOrderArtifactRepository
{
    private readonly AppDbContext _dbContext;

    public OrderArtifactRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<OrderArtifact?> GetBySourceEventIdAsync(
        Guid sourceEventId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Set<OrderArtifact>().FirstOrDefaultAsync(
            artifact => artifact.SourceEventId == sourceEventId && !artifact.IsDeleted,
            cancellationToken);

    public async Task<IReadOnlyCollection<OrderArtifact>> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Set<OrderArtifact>()
            .AsNoTracking()
            .Where(artifact => artifact.OrderId == orderId && !artifact.IsDeleted)
            .OrderBy(artifact => artifact.Type)
            .ThenByDescending(artifact => artifact.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public Task AddAsync(OrderArtifact artifact, CancellationToken cancellationToken = default) =>
        _dbContext.Set<OrderArtifact>().AddAsync(artifact, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
