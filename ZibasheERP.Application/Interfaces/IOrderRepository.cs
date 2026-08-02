using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IOrderRepository
{
    // ثبت سفارش جدید
    Task AddAsync(Order order, CancellationToken cancellationToken = default);

    // دریافت سفارش
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    // دریافت بر اساس شماره سفارش
    Task<Order?> GetByOrderNumberAsync(
        string orderNumber,
        CancellationToken cancellationToken = default);

    Task<Order?> GetByExternalReferenceAsync(
        string externalReference,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Order>> GetForAdminAsync(
        OrderStatus? status,
        int limit,
        CancellationToken cancellationToken = default);

    // بررسی تکراری نبودن شماره سفارش
    Task<bool> OrderNumberExistsAsync(
        string orderNumber,
        CancellationToken cancellationToken = default);

    // بروزرسانی سفارش
    Task UpdateAsync(Order order, CancellationToken cancellationToken = default);

    // ذخیره تغییرات
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
