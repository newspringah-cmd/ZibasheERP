using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IInvoicePaymentAccountRepository
{
    Task<IReadOnlyCollection<InvoicePaymentAccount>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<InvoicePaymentAccount>> GetForAdminAsync(CancellationToken cancellationToken = default);
    Task<InvoicePaymentAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(InvoicePaymentAccount account, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
