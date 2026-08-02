using MediatR;

namespace ZibasheERP.Application.Features.Orders.SetDeliveryAddress;

public sealed record SetOrderDeliveryAddressCommand(Guid OrderId, Guid AddressId)
    : IRequest<SetOrderDeliveryAddressResponse>;

public sealed record SetOrderDeliveryAddressResponse(
    Guid OrderId,
    Guid AddressId,
    string City,
    string FullAddress);
