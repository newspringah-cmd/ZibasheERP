using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Payments.SubmitPayment;

public sealed class SubmitPaymentCommandHandler
    : IRequestHandler<SubmitPaymentCommand, SubmitPaymentResponse>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IPaymentRepository _paymentRepository;

    public SubmitPaymentCommandHandler(
        IOrderRepository orderRepository,
        IPaymentRepository paymentRepository)
    {
        _orderRepository = orderRepository;
        _paymentRepository = paymentRepository;
    }

    public async Task<SubmitPaymentResponse> Handle(
        SubmitPaymentCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(request.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");

        if (order.Status == OrderState.Cancelled)
            throw new InvalidOperationException("برای سفارش لغوشده نمی‌توان پرداخت ثبت کرد.");

        if (order.Status == OrderState.Paid)
            throw new InvalidOperationException("این سفارش قبلاً به‌طور کامل پرداخت شده است.");

        var transactionId = request.TransactionId.Trim();
        if (await _paymentRepository.TransactionIdExistsAsync(transactionId, cancellationToken))
            throw new InvalidOperationException("این شناسه تراکنش قبلاً ثبت شده است.");

        var reservedAmount = order.Payments
            .Where(payment => !payment.IsDeleted &&
                payment.Status is PaymentStatus.Pending or PaymentStatus.Confirmed)
            .Sum(payment => payment.Amount);
        var remainingAmount = order.FinalAmount - reservedAmount;

        if (request.Amount > remainingAmount)
            throw new InvalidOperationException("مبلغ پرداخت از مانده سفارش بیشتر است.");

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            OrderId = order.Id,
            Amount = request.Amount,
            PaymentMethod = request.PaymentMethod.Trim(),
            TransactionId = transactionId,
            Status = PaymentStatus.Pending,
            IsSuccessful = false,
            Notes = NormalizeOptional(request.Notes)
        };

        await _paymentRepository.AddAsync(payment, cancellationToken);
        await _paymentRepository.SaveChangesAsync(cancellationToken);

        return new SubmitPaymentResponse(
            payment.Id,
            payment.Status.ToString(),
            payment.Amount,
            remainingAmount - payment.Amount);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
