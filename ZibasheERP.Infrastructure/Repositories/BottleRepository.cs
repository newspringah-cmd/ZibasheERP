using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class BottleRepository : IBottleRepository
{
    private readonly AppDbContext _dbContext;

    public BottleRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Bottle?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Bottles
            .FirstOrDefaultAsync(
                x => x.Id == id &&
                     x.IsActive &&
                     !x.IsDeleted,
                cancellationToken);
    }

    public async Task<Bottle?> GetByTypeAsync(
        BottleType type,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Bottles
            .FirstOrDefaultAsync(
                x => x.Type == type &&
                     x.IsDefault &&
                     x.IsActive &&
                     !x.IsDeleted,
                cancellationToken);
    }

    public async Task<List<Bottle>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Bottles
            .Where(x => x.IsActive && !x.IsDeleted)
            .OrderBy(x => x.VolumeMl)
            .ToListAsync(cancellationToken);
    }
}