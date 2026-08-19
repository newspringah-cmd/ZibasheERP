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
                .ThenInclude(batch => batch.Perfume)
            .Include(x => x.BottleOwnerCustomer)
            .FirstOrDefaultAsync(
                x => x.Id == id && !x.IsDeleted,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<SalesList>> GetOpenAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.SalesLists
            .AsNoTracking()
            .Include(salesList => salesList.Batch)
                .ThenInclude(batch => batch.Perfume)
            .Where(salesList =>
                salesList.Status == SalesListStatus.Open &&
                salesList.ReservedVolume < salesList.TotalVolume &&
                salesList.Batch.Perfume.IsActive &&
                !salesList.IsDeleted)
            .OrderByDescending(salesList => salesList.OpenDate)
            .Take(Math.Clamp(limit, 1, 50))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<SalesList>> GetForAdminAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.SalesLists
            .AsNoTracking()
            .Include(salesList => salesList.Batch)
                .ThenInclude(batch => batch.Perfume)
            .Include(salesList => salesList.BottleOwnerCustomer)
            .Where(salesList => !salesList.IsDeleted)
            .OrderByDescending(salesList => salesList.OpenDate)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArrayAsync(cancellationToken);
    }

    public Task<bool> HasActiveForBatchAsync(
        Guid batchId,
        CancellationToken cancellationToken = default) =>
        _dbContext.SalesLists.AnyAsync(
            salesList => !salesList.IsDeleted &&
                         salesList.BatchId == batchId &&
                         (salesList.Status == SalesListStatus.Open ||
                          salesList.Status == SalesListStatus.Full ||
                          salesList.Status == SalesListStatus.Purchased ||
                          salesList.Status == SalesListStatus.Invoiced),
            cancellationToken);

    public Task<bool> PublicCodeExistsAsync(int publicCode, CancellationToken cancellationToken = default) =>
        _dbContext.SalesLists.AnyAsync(
            salesList => !salesList.IsDeleted && salesList.PublicCode == publicCode,
            cancellationToken);

    public Task AddAsync(
        SalesList salesList,
        CancellationToken cancellationToken = default) =>
        _dbContext.SalesLists.AddAsync(salesList, cancellationToken).AsTask();

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
