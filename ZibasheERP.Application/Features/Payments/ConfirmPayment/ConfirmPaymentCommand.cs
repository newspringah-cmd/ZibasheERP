using MediatR;

namespace ZibasheERP.Application.Features.Payments.ConfirmPayment;

public sealed record ConfirmPaymentCommand(Guid PaymentId) : IRequest<ConfirmPaymentResponse>;

public sealed record ConfirmPaymentResponse(
    Guid PaymentId,
    Guid OrderId,
    string PaymentStatus,
    string OrderStatus,
    decimal RemainingAmount);
