using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Payments.ConfirmPayment;

public sealed class ConfirmPaymentCommandHandler
    : IRequestHandler<ConfirmPaymentCommand, ConfirmPaymentResponse>
{
    private readonly IPaymentRepository _paymentRepository;

    public ConfirmPaymentCommandHandler(IPaymentRepository paymentRepository)
    {
        _paymentRepository = paymentRepository;
    }

    public async Task<ConfirmPaymentResponse> Handle(
        ConfirmPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await _paymentRepository.GetByIdAsync(request.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException("پرداخت پیدا نشد.");
        var order = payment.Order
            ?? throw new InvalidOperationException("سفارش مرتبط با پرداخت پیدا نشد.");
        var customer = order.Customer
            ?? throw new InvalidOperationException("مشتری مرتبط با سفارش پیدا نشد.");

        if (order.Status == OrderState.Cancelled)
            throw new InvalidOperationException("پرداخت سفارش لغوشده قابل تأیید نیست.");

        if (payment.Status == PaymentStatus.Confirmed)
            return BuildResponse(payment, order);

        if (payment.Status != PaymentStatus.Pending)
            throw new InvalidOperationException("فقط پرداخت در انتظار بررسی قابل تأیید است.");

        var confirmedBefore = order.Payments
            .Where(value => value.Id != payment.Id &&
                !value.IsDeleted &&
                value.Status == PaymentStatus.Confirmed)
            .Sum(value => value.Amount);

        if (confirmedBefore + payment.Amount > order.FinalAmount)
            throw new InvalidOperationException("تأیید این پرداخت باعث اضافه‌پرداخت می‌شود.");

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Confirmed;
        payment.IsSuccessful = true;
        payment.PaidAt = now;
        payment.UpdatedAt = now;

        customer.CurrentDebt = Math.Max(0, customer.CurrentDebt - payment.Amount);
        customer.UpdatedAt = now;

        if (confirmedBefore + payment.Amount == order.FinalAmount)
        {
            order.Status = OrderState.Paid;
            order.PaidAt = now;
        }

        order.UpdatedAt = now;
        await _paymentRepository.SaveChangesAsync(cancellationToken);

        return BuildResponse(payment, order);
    }

    private static ConfirmPaymentResponse BuildResponse(Payment payment, Order order)
    {
        var confirmedAmount = order.Payments
            .Where(value => !value.IsDeleted && value.Status == PaymentStatus.Confirmed)
            .Sum(value => value.Amount);

        return new ConfirmPaymentResponse(
            payment.Id,
            order.Id,
            payment.Status.ToString(),
            order.Status.ToString(),
            Math.Max(0, order.FinalAmount - confirmedAmount));
    }
}
