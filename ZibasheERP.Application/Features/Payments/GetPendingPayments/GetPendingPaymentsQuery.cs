using MediatR;

namespace ZibasheERP.Application.Features.Payments.GetPendingPayments;

public sealed record GetPendingPaymentsQuery(int Limit = 50)
    : IRequest<IReadOnlyCollection<PendingPaymentResponse>>;

public sealed record PendingPaymentResponse(
    Guid PaymentId,
    Guid OrderId,
    string OrderNumber,
    string CustomerName,
    string Mobile,
    string? TelegramId,
    decimal Amount,
    string PaymentMethod,
    string? TransactionId,
    DateTime SubmittedAt);
