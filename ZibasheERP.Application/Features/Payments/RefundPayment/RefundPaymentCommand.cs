using MediatR;

namespace ZibasheERP.Application.Features.Payments.RefundPayment;

public sealed record RefundPaymentCommand(Guid PaymentId, string Reason)
    : IRequest<RefundPaymentResponse>;

public sealed record RefundPaymentResponse(
    Guid PaymentId,
    Guid OrderId,
    string PaymentStatus,
    string OrderStatus,
    decimal Amount,
    string Reason);
