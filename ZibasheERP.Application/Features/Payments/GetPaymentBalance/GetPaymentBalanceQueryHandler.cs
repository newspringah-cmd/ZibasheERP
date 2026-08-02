using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.Features.Payments.GetPaymentBalance;

public sealed class GetPaymentBalanceQueryHandler
    : IRequestHandler<GetPaymentBalanceQuery, PaymentBalanceResponse?>
{
    private readonly IOrderRepository _orderRepository;

    public GetPaymentBalanceQueryHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<PaymentBalanceResponse?> Handle(
        GetPaymentBalanceQuery request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByOrderNumberAsync(
            request.OrderNumber.Trim(),
            cancellationToken);
        if (order?.Customer is null)
            return null;

        var reserved = order.Payments
            .Where(payment => !payment.IsDeleted &&
                payment.Status is PaymentStatus.Pending or PaymentStatus.Confirmed)
            .Sum(payment => payment.Amount);

        return new PaymentBalanceResponse(
            order.Id,
            order.OrderNumber,
            order.Customer.TelegramId,
            order.Status.ToString(),
            Math.Max(0, order.FinalAmount - reserved));
    }
}
