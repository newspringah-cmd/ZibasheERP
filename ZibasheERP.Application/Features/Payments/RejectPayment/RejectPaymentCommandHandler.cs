using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.Features.Payments.RejectPayment;

public sealed class RejectPaymentCommandHandler
    : IRequestHandler<RejectPaymentCommand, RejectPaymentResponse>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public RejectPaymentCommandHandler(
        IPaymentRepository paymentRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _paymentRepository = paymentRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<RejectPaymentResponse> Handle(
        RejectPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await _paymentRepository.GetByIdAsync(request.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException("پرداخت پیدا نشد.");
        var order = payment.Order
            ?? throw new InvalidOperationException("سفارش مرتبط با پرداخت پیدا نشد.");
        var reason = request.Reason.Trim();
        if (reason.Length is < 3 or > 500)
            throw new InvalidOperationException("علت رد پرداخت باید بین ۳ تا ۵۰۰ کاراکتر باشد.");

        if (payment.Status == PaymentStatus.Confirmed)
            throw new InvalidOperationException("پرداخت تأییدشده قابل رد نیست.");
        if (payment.Status == PaymentStatus.Rejected)
            return new(payment.Id, order.Id, payment.Status.ToString(), payment.Notes ?? reason);
        if (payment.Status != PaymentStatus.Pending)
            throw new InvalidOperationException("فقط پرداخت در انتظار قابل رد است.");

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Rejected;
        payment.IsSuccessful = false;
        payment.Notes = reason;
        payment.UpdatedAt = now;

        var notification = TelegramNotificationFactory.Create(
            order,
            "PaymentRejected",
            new { order.Id, order.OrderNumber, Reason = reason },
            now);
        if (notification is not null)
            await _outboxRepository.AddAsync(notification, cancellationToken);

        await _paymentRepository.SaveChangesAsync(cancellationToken);
        return new(payment.Id, order.Id, payment.Status.ToString(), reason);
    }
}
