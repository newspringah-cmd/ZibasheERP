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
}