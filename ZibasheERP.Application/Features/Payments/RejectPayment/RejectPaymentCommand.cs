using MediatR;

namespace ZibasheERP.Application.Features.Payments.RejectPayment;

public sealed record RejectPaymentCommand(Guid PaymentId, string Reason)
    : IRequest<RejectPaymentResponse>;

public sealed record RejectPaymentResponse(
    Guid PaymentId,
    Guid OrderId,
    string Status,
    string Reason);
