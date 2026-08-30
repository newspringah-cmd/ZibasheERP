using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class InvoicePaymentStatusService(AppDbContext db) : IInvoicePaymentStatusService
{
    public async Task<InvoicePaymentStatusResult> MarkPaidAsync(
        Guid invoiceId,
        long confirmedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var invoice = await Query().FirstOrDefaultAsync(
            value => value.Id == invoiceId && !value.IsDeleted,
            cancellationToken) ?? throw new InvalidOperationException("فاکتور پیدا نشد.");
        var order = invoice.Order ?? throw new InvalidOperationException("سفارش فاکتور پیدا نشد.");
        var customer = order.Customer ?? throw new InvalidOperationException("مشتری فاکتور پیدا نشد.");
        if (order.Status == OrderStatus.Cancelled || invoice.Status == InvoiceStatus.Cancelled)
            throw new InvalidOperationException("فاکتور لغوشده قابل پرداخت نیست.");
        if (invoice.Status == InvoiceStatus.Paid && order.Status == OrderStatus.Paid)
            return Build(invoice, order);

        var confirmedAmount = order.Payments
            .Where(value => !value.IsDeleted && value.Status == PaymentStatus.Confirmed)
            .Sum(value => value.Amount);
        var remaining = Math.Max(0, order.FinalAmount - confirmedAmount);
        var pending = order.Payments
            .Where(value => !value.IsDeleted && value.Status == PaymentStatus.Pending)
            .OrderBy(value => value.CreatedAt)
            .ToArray();
        var pendingAmount = pending.Sum(value => value.Amount);
        if (pendingAmount > remaining)
            throw new InvalidOperationException("جمع فیش‌های در انتظار از مانده فاکتور بیشتر است؛ ابتدا فیش‌ها را بررسی کنید.");

        var now = DateTime.UtcNow;
        foreach (var payment in pending)
        {
            payment.Status = PaymentStatus.Confirmed;
            payment.IsSuccessful = true;
            payment.PaidAt = now;
            payment.UpdatedAt = now;
        }
        var adjustment = remaining - pendingAmount;
        if (adjustment > 0)
        {
            order.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(), CreatedAt = now, OrderId = order.Id,
                Amount = adjustment, PaymentMethod = "AdminTelegram",
                TransactionId = $"admin-{invoice.Id:N}", Status = PaymentStatus.Confirmed,
                IsSuccessful = true, PaidAt = now,
                Notes = $"تأیید دستی فاکتور توسط مدیر تلگرام {confirmedByTelegramUserId}"
            });
        }

        customer.CurrentDebt = Math.Max(0, customer.CurrentDebt - remaining);
        customer.UpdatedAt = now;
        invoice.Status = InvoiceStatus.Paid;
        invoice.UpdatedAt = now;
        order.Status = OrderStatus.Paid;
        order.PaidAt = now;
        order.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Build(invoice, order);
    }

    public async Task<InvoicePaymentStatusResult> KeepWaitingAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        var invoice = await Query().FirstOrDefaultAsync(
            value => value.Id == invoiceId && !value.IsDeleted,
            cancellationToken) ?? throw new InvalidOperationException("فاکتور پیدا نشد.");
        var order = invoice.Order ?? throw new InvalidOperationException("سفارش فاکتور پیدا نشد.");
        if (invoice.Status == InvoiceStatus.Paid || order.Status == OrderStatus.Paid)
            throw new InvalidOperationException("فاکتور پرداخت شده است؛ برای برگشت مالی باید از فرایند استرداد استفاده شود.");
        return Build(invoice, order);
    }

    private IQueryable<Invoice> Query() => db.Invoices
        .Include(value => value.Order)
            .ThenInclude(value => value!.Customer)
        .Include(value => value.Order)
            .ThenInclude(value => value!.Payments);

    private static InvoicePaymentStatusResult Build(Invoice invoice, Order order) =>
        new(invoice.Id, invoice.InvoiceNumber,
            invoice.Status == InvoiceStatus.Paid && order.Status == OrderStatus.Paid,
            order.PaidAt,
            order.InvoiceIssuanceBatchId);
}
