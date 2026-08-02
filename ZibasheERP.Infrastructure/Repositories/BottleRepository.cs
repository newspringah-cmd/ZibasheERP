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

    public async Task<IReadOnlyCollection<Bottle>> GetForAdminAsync(
        bool includeInactive,
        int limit,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bottles
            .AsNoTracking()
            .Where(bottle => !bottle.IsDeleted && (includeInactive || bottle.IsActive))
            .OrderBy(bottle => bottle.VolumeMl)
            .ThenBy(bottle => bottle.Type)
            .ThenBy(bottle => bottle.Name)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArrayAsync(cancellationToken);

    public Task<Bottle?> GetForAdminByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Bottles.FirstOrDefaultAsync(
            bottle => bottle.Id == id && !bottle.IsDeleted,
            cancellationToken);

    public Task<bool> ExistsAsync(
        string name,
        int volumeMl,
        BottleType type,
        CancellationToken cancellationToken = default) =>
        _dbContext.Bottles.AnyAsync(
            bottle => !bottle.IsDeleted && bottle.Name == name &&
                      bottle.VolumeMl == volumeMl && bottle.Type == type,
            cancellationToken);

    public Task<bool> DefaultExistsAsync(
        int volumeMl,
        BottleType type,
        CancellationToken cancellationToken = default) =>
        _dbContext.Bottles.AnyAsync(
            bottle => !bottle.IsDeleted && bottle.IsDefault &&
                      bottle.VolumeMl == volumeMl && bottle.Type == type,
            cancellationToken);

    public Task AddAsync(Bottle bottle, CancellationToken cancellationToken = default) =>
        _dbContext.Bottles.AddAsync(bottle, cancellationToken).AsTask();

    public Task UpdateAsync(Bottle bottle, CancellationToken cancellationToken = default)
    {
        _dbContext.Bottles.Update(bottle);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
