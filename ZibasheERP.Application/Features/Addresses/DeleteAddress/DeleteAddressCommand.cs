using MediatR;

namespace ZibasheERP.Application.Features.Addresses.DeleteAddress;

public sealed record DeleteAddressCommand(
    Guid AddressId,
    Guid? CustomerId,
    string? TelegramId) : IRequest;
