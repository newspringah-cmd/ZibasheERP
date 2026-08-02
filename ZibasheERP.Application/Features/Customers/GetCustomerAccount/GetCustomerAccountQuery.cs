using MediatR;

namespace ZibasheERP.Application.Features.Customers.GetCustomerAccount;

public sealed record GetCustomerAccountQuery(Guid? CustomerId, string? TelegramId)
    : IRequest<CustomerAccountResponse?>;

public sealed record CustomerAccountResponse(
    Guid CustomerId,
    string FullName,
    string Mobile,
    string? TelegramId,
    string? Username,
    decimal WalletBalance,
    decimal CreditLimit,
    decimal CurrentDebt,
    decimal AvailableCredit,
    bool IsBlocked,
    bool CanPlaceOrder);
