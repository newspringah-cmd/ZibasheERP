using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Customers.GetCustomerAccount;

public sealed class GetCustomerAccountQueryHandler
    : IRequestHandler<GetCustomerAccountQuery, CustomerAccountResponse?>
{
    private readonly ICustomerRepository _repository;
    public GetCustomerAccountQueryHandler(ICustomerRepository repository) => _repository = repository;

    public async Task<CustomerAccountResponse?> Handle(
        GetCustomerAccountQuery request,
        CancellationToken cancellationToken)
    {
        var hasCustomerId = request.CustomerId.HasValue && request.CustomerId.Value != Guid.Empty;
        var hasTelegramId = !string.IsNullOrWhiteSpace(request.TelegramId);
        if (hasCustomerId == hasTelegramId)
            throw new InvalidOperationException("دقیقاً یک شناسه مشتری یا تلگرام باید ارسال شود.");

        var customer = hasCustomerId
            ? await _repository.GetByIdAsync(request.CustomerId!.Value, cancellationToken)
            : await _repository.GetByTelegramIdAsync(request.TelegramId!.Trim(), cancellationToken);
        if (customer is null)
            return null;

        return new CustomerAccountResponse(
            customer.Id,
            customer.FullName,
            customer.Mobile,
            customer.TelegramId,
            customer.Username,
            customer.WalletBalance,
            customer.CreditLimit,
            customer.CurrentDebt,
            customer.AvailableCredit,
            customer.IsBlocked,
            customer.CanPlaceOrder);
    }
}
