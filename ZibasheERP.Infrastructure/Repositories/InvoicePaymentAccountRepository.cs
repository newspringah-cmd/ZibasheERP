using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class InvoicePaymentAccountRepository(AppDbContext dbContext)
    : IInvoicePaymentAccountRepository
{
    public async Task<IReadOnlyCollection<InvoicePaymentAccount>> GetActiveAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.InvoicePaymentAccounts.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive)
            .OrderBy(value => value.DisplayOrder).ThenBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<InvoicePaymentAccount>> GetForAdminAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.InvoicePaymentAccounts.AsNoTracking()
            .Where(value => !value.IsDeleted)
            .OrderBy(value => value.DisplayOrder).ThenBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public Task<InvoicePaymentAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.InvoicePaymentAccounts.FirstOrDefaultAsync(
            value => value.Id == id && !value.IsDeleted, cancellationToken);

    public Task AddAsync(InvoicePaymentAccount account, CancellationToken cancellationToken = default) =>
        dbContext.InvoicePaymentAccounts.AddAsync(account, cancellationToken).AsTask();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
