using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Customers.ManageCustomers;

public sealed class SearchCustomersQueryHandler
    : IRequestHandler<SearchCustomersQuery, IReadOnlyCollection<AdminCustomerResponse>>
{
    private readonly IAdminCustomerRepository _repository;
    public SearchCustomersQueryHandler(IAdminCustomerRepository repository) => _repository = repository;

    public async Task<IReadOnlyCollection<AdminCustomerResponse>> Handle(
        SearchCustomersQuery request,
        CancellationToken cancellationToken)
    {
        var customers = await _repository.SearchAsync(
            request.Search,
            request.DebtOnly,
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return customers.Select(CustomerManagementMapper.ToResponse).ToArray();
    }
}

public sealed class SetCustomerAccessCommandHandler
    : IRequestHandler<SetCustomerAccessCommand, AdminCustomerResponse>
{
    private readonly IAdminCustomerRepository _repository;
    public SetCustomerAccessCommandHandler(IAdminCustomerRepository repository) => _repository = repository;

    public async Task<AdminCustomerResponse> Handle(
        SetCustomerAccessCommand request,
        CancellationToken cancellationToken)
    {
        var customer = await GetCustomer(request.CustomerId, cancellationToken);
        customer.IsBlocked = request.IsBlocked;
        customer.CanPlaceOrder = request.CanPlaceOrder && !request.IsBlocked;
        customer.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(customer, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return CustomerManagementMapper.ToResponse(customer);
    }

    private async Task<Customer> GetCustomer(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetByIdAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("مشتری پیدا نشد.");
}

public sealed class SetCustomerCreditCommandHandler
    : IRequestHandler<SetCustomerCreditCommand, AdminCustomerResponse>
{
    private readonly IAdminCustomerRepository _repository;
    public SetCustomerCreditCommandHandler(IAdminCustomerRepository repository) => _repository = repository;

    public async Task<AdminCustomerResponse> Handle(
        SetCustomerCreditCommand request,
        CancellationToken cancellationToken)
    {
        var customer = await _repository.GetByIdAsync(request.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException("مشتری پیدا نشد.");
        customer.CreditLimit = request.CreditLimit;
        customer.WalletBalance = request.WalletBalance;
        customer.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(customer, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return CustomerManagementMapper.ToResponse(customer);
    }
}

internal static class CustomerManagementMapper
{
    internal static AdminCustomerResponse ToResponse(Customer customer) => new(
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
        customer.CanPlaceOrder,
        customer.LastOrderAt,
        customer.Notes);
}
