using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Payments.RefundPayment;

public sealed class RefundPaymentCommandHandler
    : IRequestHandler<RefundPaymentCommand, RefundPaymentResponse>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public RefundPaymentCommandHandler(
        IPaymentRepository paymentRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _paymentRepository = paymentRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<RefundPaymentResponse> Handle(
        RefundPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await _paymentRepository.GetByIdAsync(request.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException("پرداخت پیدا نشد.");
        var order = payment.Order
            ?? throw new InvalidOperationException("سفارش مرتبط با پرداخت پیدا نشد.");
        var customer = order.Customer
            ?? throw new InvalidOperationException("مشتری سفارش پیدا نشد.");
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
            throw new InvalidOperationException("علت بازپرداخت باید بین ۳ تا ۵۰۰ کاراکتر باشد.");

        if (payment.Status == PaymentStatus.Refunded)
            return BuildResponse(payment, order, reason);
        if (payment.Status != PaymentStatus.Confirmed)
            throw new InvalidOperationException("فقط پرداخت تأییدشده قابل بازپرداخت است.");
        if (order.Status is OrderState.Shipped or OrderState.Delivered)
            throw new InvalidOperationException("پرداخت سفارش ارسال‌شده یا تحویل‌شده قابل بازپرداخت نیست.");

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Refunded;
        payment.IsSuccessful = false;
        payment.Notes = AppendReason(payment.Notes, reason);
        payment.UpdatedAt = now;

        customer.CurrentDebt += payment.Amount;
        customer.UpdatedAt = now;
        order.Status = order.InvoiceIssuedAt.HasValue
            ? OrderState.Invoiced
            : OrderState.Registered;
        order.PaidAt = null;
        order.UpdatedAt = now;

        var notification = TelegramNotificationFactory.Create(
            order,
            "PaymentRefunded",
            new { order.Id, order.OrderNumber, payment.Amount, Reason = reason },
            now);
        if (notification is not null)
            await _outboxRepository.AddAsync(notification, cancellationToken);

        await _paymentRepository.SaveChangesAsync(cancellationToken);
        return BuildResponse(payment, order, reason);
    }

    private static RefundPaymentResponse BuildResponse(
        ZibasheERP.Domain.Entities.Payment payment,
        ZibasheERP.Domain.Entities.Order order,
        string reason) => new(
            payment.Id,
            order.Id,
            payment.Status.ToString(),
            order.Status.ToString(),
            payment.Amount,
            reason);

    private static string AppendReason(string? notes, string reason)
    {
        var value = string.IsNullOrWhiteSpace(notes)
            ? $"Refund: {reason}"
            : $"{notes.Trim()}\nRefund: {reason}";
        return value.Length <= 500 ? value : value[..500];
    }
}
