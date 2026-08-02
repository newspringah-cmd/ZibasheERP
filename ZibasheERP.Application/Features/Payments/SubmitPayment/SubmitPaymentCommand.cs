using MediatR;

namespace ZibasheERP.Application.Features.Payments.SubmitPayment;

public sealed record SubmitPaymentCommand(
    Guid OrderId,
    decimal Amount,
    string PaymentMethod,
    string TransactionId,
    string? Notes) : IRequest<SubmitPaymentResponse>;

public sealed record SubmitPaymentResponse(
    Guid PaymentId,
    string Status,
    decimal Amount,
    decimal RemainingAmount);
