using MediatR;

namespace ZibasheERP.Application.Features.Payments.GetPaymentBalance;

public sealed record GetPaymentBalanceQuery(string OrderNumber)
    : IRequest<PaymentBalanceResponse?>;

public sealed record PaymentBalanceResponse(
    Guid OrderId,
    string OrderNumber,
    string? TelegramId,
    string OrderStatus,
    decimal RemainingAmount);
