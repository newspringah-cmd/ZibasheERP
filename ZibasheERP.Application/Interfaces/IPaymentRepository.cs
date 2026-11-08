using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IPaymentRepository
{
    Task AddAsync(Payment payment, CancellationToken cancellationToken = default);
    Task AddForOrderVersionAsync(Payment payment,Guid customerId,byte[] orderVersion,CancellationToken cancellationToken=default)
        =>AddAsync(payment,cancellationToken);
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Payment>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> TransactionIdExistsAsync(string transactionId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
