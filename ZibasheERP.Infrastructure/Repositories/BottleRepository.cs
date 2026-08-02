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
                     !x.IsDeleted &&
                     x.IsActive,
                cancellationToken);
    }
}