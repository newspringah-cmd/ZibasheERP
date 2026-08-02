using MediatR;
using ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;

namespace ZibasheERP.Application.Features.Addresses.AddTelegramAddress;

public sealed record AddTelegramAddressCommand(
    string TelegramId,
    string Description,
    string ReceiverName,
    string Province,
    string City,
    string PostalCode,
    string FullAddress) : IRequest<CustomerAddressResponse>;
