using MediatR;

namespace ZibasheERP.Application.Features.Customers.ManageCustomers;

public sealed record SearchCustomersQuery(
    string? Search = null,
    bool DebtOnly = false,
    int Limit = 100) : IRequest<IReadOnlyCollection<AdminCustomerResponse>>;

public sealed record SetCustomerAccessCommand(
    Guid CustomerId,
    bool IsBlocked,
    bool CanPlaceOrder) : IRequest<AdminCustomerResponse>;

public sealed record SetCustomerCreditCommand(
    Guid CustomerId,
    decimal CreditLimit,
    decimal WalletBalance) : IRequest<AdminCustomerResponse>;

public sealed record AdminCustomerResponse(
    Guid Id,
    string FullName,
    string Mobile,
    string? TelegramId,
    string? Username,
    decimal WalletBalance,
    decimal CreditLimit,
    decimal CurrentDebt,
    decimal AvailableCredit,
    bool IsBlocked,
    bool CanPlaceOrder,
    DateTime? LastOrderAt,
    string? Notes);
