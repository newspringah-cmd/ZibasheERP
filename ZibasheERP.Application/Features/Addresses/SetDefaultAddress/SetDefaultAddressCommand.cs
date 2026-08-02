using MediatR;

namespace ZibasheERP.Application.Features.Addresses.SetDefaultAddress;

public sealed record SetDefaultAddressCommand(
    Guid AddressId,
    Guid? CustomerId,
    string? TelegramId) : IRequest;
