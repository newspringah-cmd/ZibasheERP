using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;

public sealed class GetCustomerAddressesQueryHandler
    : IRequestHandler<GetCustomerAddressesQuery, IReadOnlyCollection<CustomerAddressResponse>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IAddressRepository _addressRepository;

    public GetCustomerAddressesQueryHandler(
        ICustomerRepository customerRepository,
        IAddressRepository addressRepository)
    {
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
    }

    public async Task<IReadOnlyCollection<CustomerAddressResponse>> Handle(
        GetCustomerAddressesQuery request,
        CancellationToken cancellationToken)
    {
        var customer = request.CustomerId is { } customerId && customerId != Guid.Empty
            ? await _customerRepository.GetByIdAsync(customerId, cancellationToken)
            : string.IsNullOrWhiteSpace(request.TelegramId)
                ? null
                : await _customerRepository.GetByTelegramIdAsync(
                    request.TelegramId.Trim(),
                    cancellationToken);
        if (customer is null)
            return Array.Empty<CustomerAddressResponse>();

        var addresses = await _addressRepository.GetByCustomerIdAsync(
            customer.Id,
            cancellationToken);
        return addresses.Select(address => new CustomerAddressResponse(
            address.Id,
            address.ReceiverName,
            address.Mobile,
            address.Province,
            address.City,
            address.PostalCode,
            address.FullAddress,
            address.Description,
            address.IsDefault))
            .ToArray();
    }
}
