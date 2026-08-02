using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class PaymentRepository : IPaymentRepository
{
    private readonly AppDbContext _dbContext;

    public PaymentRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        await _dbContext.Payments.AddAsync(payment, cancellationToken);
    }

    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Payments
            .Include(payment => payment.Order)
                .ThenInclude(order => order!.Customer)
            .Include(payment => payment.Order)
                .ThenInclude(order => order!.Payments)
            .FirstOrDefaultAsync(
                payment => payment.Id == id && !payment.IsDeleted,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<Payment>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Include(payment => payment.Order)
                .ThenInclude(order => order!.Customer)
            .Where(payment =>
                payment.Status == ZibasheERP.Domain.Enums.PaymentStatus.Pending &&
                !payment.IsDeleted)
            .OrderBy(payment => payment.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArrayAsync(cancellationToken);
    }

    public Task<bool> TransactionIdExistsAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Payments.AnyAsync(
            payment => payment.TransactionId == transactionId,
            cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
