using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class AddressRepository : IAddressRepository
{
    private readonly AppDbContext _dbContext;

    public AddressRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Addresses.FirstOrDefaultAsync(
            address => address.Id == id && !address.IsDeleted,
            cancellationToken);

    public async Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Addresses
            .Where(address => address.CustomerId == customerId && !address.IsDeleted)
            .OrderByDescending(address => address.IsDefault)
            .ThenBy(address => address.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(Address address, CancellationToken cancellationToken = default) =>
        await _dbContext.Addresses.AddAsync(address, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SaveChangesAsync(cancellationToken);
}
