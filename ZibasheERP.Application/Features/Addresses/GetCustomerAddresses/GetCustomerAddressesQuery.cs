using MediatR;

namespace ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;

public sealed record GetCustomerAddressesQuery(
    Guid? CustomerId,
    string? TelegramId) : IRequest<IReadOnlyCollection<CustomerAddressResponse>>;

public sealed record CustomerAddressResponse(
    Guid Id,
    string ReceiverName,
    string Mobile,
    string Province,
    string City,
    string PostalCode,
    string FullAddress,
    string? Description,
    bool IsDefault);
