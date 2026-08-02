using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class BatchRepository : IBatchRepository
{
    private readonly AppDbContext _dbContext;

    public BatchRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Batch?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Batches
            .Include(x => x.Perfume)
            .FirstOrDefaultAsync(
                x => x.Id == id &&
                     !x.IsDeleted,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<Batch>> GetForInventoryAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Batches
            .AsNoTracking()
            .Include(batch => batch.Perfume)
            .Where(batch => !batch.IsDeleted && !batch.Perfume.IsDeleted)
            .OrderByDescending(batch => batch.PurchaseDate)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArrayAsync(cancellationToken);
    }

    public Task UpdateAsync(
        Batch batch,
        CancellationToken cancellationToken = default)
    {
        _dbContext.Batches.Update(batch);
        return Task.CompletedTask;
    }

    public Task<bool> BatchNumberExistsAsync(
        string batchNumber,
        CancellationToken cancellationToken = default) =>
        _dbContext.Batches.AnyAsync(
            batch => !batch.IsDeleted && batch.BatchNumber == batchNumber,
            cancellationToken);

    public Task AddAsync(
        Batch batch,
        CancellationToken cancellationToken = default) =>
        _dbContext.Batches.AddAsync(batch, cancellationToken).AsTask();

    public async Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
