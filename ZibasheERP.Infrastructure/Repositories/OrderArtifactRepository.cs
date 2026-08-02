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

    public Task AddAsync(OrderArtifact artifact, CancellationToken cancellationToken = default) =>
        _dbContext.Set<OrderArtifact>().AddAsync(artifact, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
