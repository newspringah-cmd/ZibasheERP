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
    public async Task AddForOrderVersionAsync(Payment payment,Guid customerId,byte[] orderVersion,CancellationToken cancellationToken=default)
    {
        await using var tx=await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var order=await _dbContext.Orders.FromSqlInterpolated($"SELECT * FROM Orders WITH(UPDLOCK,HOLDLOCK) WHERE Id={payment.OrderId}").AsNoTracking().SingleAsync(cancellationToken);
        if(order.IsDeleted||order.CustomerId!=customerId||!order.RowVersion.SequenceEqual(orderVersion))
            throw new InvalidOperationException("مالکیت یا وضعیت سفارش تغییر کرده است؛ فاکتور جدید را باز کنید و دوباره رسید بفرستید.");
        var reserved=await _dbContext.Payments.Where(p=>p.OrderId==order.Id&&!p.IsDeleted&&
            (p.Status==ZibasheERP.Domain.Enums.PaymentStatus.Pending||p.Status==ZibasheERP.Domain.Enums.PaymentStatus.Confirmed)).SumAsync(p=>p.Amount,cancellationToken);
        if(order.Status is OrderStatus.Paid or OrderStatus.Cancelled||payment.Amount>order.FinalAmount-reserved)
            throw new InvalidOperationException("مانده یا وضعیت سفارش تغییر کرده است؛ فاکتور را دوباره باز کنید.");
        await _dbContext.Orders.Where(o=>o.Id==order.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.UpdatedAt,DateTime.UtcNow),cancellationToken);
        await _dbContext.Payments.AddAsync(payment,cancellationToken);await _dbContext.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);
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
