using MediatR;

namespace ZibasheERP.Application.Features.Customers.LinkTelegram;

public sealed record LinkTelegramCustomerCommand(
    string TelegramId,
    string Mobile,
    string? Username) : IRequest<LinkTelegramCustomerResult>;

public enum LinkTelegramCustomerStatus
{
    Linked = 1,
    AlreadyLinked = 2,
    InvalidMobile = 3,
    CustomerNotFound = 4,
    TelegramAlreadyLinked = 5,
    CustomerLinkedToAnotherTelegram = 6,
    UsernameNotFound = 7,
    UsernameLinkedToAnotherTelegram = 8
}

public sealed record LinkTelegramCustomerResult(
    LinkTelegramCustomerStatus Status,
    string? CustomerName = null);
