using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public class SalesListRepository : ISalesListRepository
{
    private readonly AppDbContext _dbContext;

    public SalesListRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SalesList?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.SalesLists
            .Include(x => x.Batch)
            .Include(x => x.BottleOwnerCustomer)
            .FirstOrDefaultAsync(
                x => x.Id == id && !x.IsDeleted,
                cancellationToken);
    }

    public Task UpdateAsync(
        SalesList salesList,
        CancellationToken cancellationToken = default)
    {
        _dbContext.SalesLists.Update(salesList);

        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}