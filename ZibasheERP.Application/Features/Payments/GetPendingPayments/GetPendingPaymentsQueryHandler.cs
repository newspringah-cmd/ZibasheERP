using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Payments.GetPendingPayments;

public sealed class GetPendingPaymentsQueryHandler
    : IRequestHandler<GetPendingPaymentsQuery, IReadOnlyCollection<PendingPaymentResponse>>
{
    private readonly IPaymentRepository _paymentRepository;

    public GetPendingPaymentsQueryHandler(IPaymentRepository paymentRepository)
    {
        _paymentRepository = paymentRepository;
    }

    public async Task<IReadOnlyCollection<PendingPaymentResponse>> Handle(
        GetPendingPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        var payments = await _paymentRepository.GetPendingAsync(
            Math.Clamp(request.Limit, 1, 100),
            cancellationToken);

        return payments
            .Where(payment => payment.Order?.Customer is not null)
            .Select(payment => new PendingPaymentResponse(
                payment.Id,
                payment.OrderId,
                payment.Order!.OrderNumber,
                payment.Order.Customer!.FullName,
                payment.Order.Customer.Mobile,
                payment.Order.Customer.TelegramId,
                payment.Amount,
                payment.PaymentMethod,
                payment.TransactionId,
                payment.CreatedAt))
            .ToArray();
    }
}
