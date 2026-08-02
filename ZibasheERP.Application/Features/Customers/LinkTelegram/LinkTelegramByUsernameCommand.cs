using MediatR;

namespace ZibasheERP.Application.Features.Customers.LinkTelegram;

public sealed record LinkTelegramByUsernameCommand(
    string TelegramId,
    string? Username) : IRequest<LinkTelegramCustomerResult>;
