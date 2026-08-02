using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class PerfumeRepository : IPerfumeRepository
{
    private readonly AppDbContext _dbContext;

    public PerfumeRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<Perfume>> GetAllAsync(
        bool includeInactive,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Perfumes
            .AsNoTracking()
            .Where(perfume => !perfume.IsDeleted && (includeInactive || perfume.IsActive))
            .OrderBy(perfume => perfume.Brand)
            .ThenBy(perfume => perfume.EnglishName)
            .Take(Math.Clamp(limit, 1, 200))
            .ToArrayAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(
        string brand,
        string englishName,
        CancellationToken cancellationToken = default) =>
        _dbContext.Perfumes.AnyAsync(
            perfume => !perfume.IsDeleted &&
                       perfume.Brand == brand &&
                       perfume.EnglishName == englishName,
            cancellationToken);

    public Task AddAsync(Perfume perfume, CancellationToken cancellationToken = default) =>
        _dbContext.Perfumes.AddAsync(perfume, cancellationToken).AsTask();

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
