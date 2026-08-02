using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Customers.LinkTelegram;

public sealed class LinkTelegramByUsernameCommandHandler
    : IRequestHandler<LinkTelegramByUsernameCommand, LinkTelegramCustomerResult>
{
    private readonly ICustomerRepository _customerRepository;

    public LinkTelegramByUsernameCommandHandler(ICustomerRepository customerRepository)
    {
        _customerRepository = customerRepository;
    }

    public async Task<LinkTelegramCustomerResult> Handle(
        LinkTelegramByUsernameCommand request,
        CancellationToken cancellationToken)
    {
        var telegramId = request.TelegramId.Trim();
        var username = NormalizeUsername(request.Username);
        if (username is null)
            return new(LinkTelegramCustomerStatus.UsernameNotFound);

        var telegramCustomer = await _customerRepository.GetByTelegramIdAsync(
            telegramId,
            cancellationToken);
        if (telegramCustomer is not null)
        {
            if (!string.Equals(
                    NormalizeUsername(telegramCustomer.Username),
                    username,
                    StringComparison.OrdinalIgnoreCase))
            {
                telegramCustomer.Username = username;
                telegramCustomer.UpdatedAt = DateTime.UtcNow;
                await _customerRepository.UpdateAsync(telegramCustomer, cancellationToken);
                await _customerRepository.SaveChangesAsync(cancellationToken);
            }

            return new(LinkTelegramCustomerStatus.AlreadyLinked, telegramCustomer.FullName);
        }

        var customer = await _customerRepository.GetByUsernameAsync(username, cancellationToken);
        if (customer is null)
            return new(LinkTelegramCustomerStatus.UsernameNotFound);

        if (!string.IsNullOrWhiteSpace(customer.TelegramId) && customer.TelegramId != telegramId)
            return new(LinkTelegramCustomerStatus.UsernameLinkedToAnotherTelegram);

        customer.TelegramId = telegramId;
        customer.Username = username;
        customer.UpdatedAt = DateTime.UtcNow;
        await _customerRepository.UpdateAsync(customer, cancellationToken);
        await _customerRepository.SaveChangesAsync(cancellationToken);

        return new(LinkTelegramCustomerStatus.Linked, customer.FullName);
    }

    private static string? NormalizeUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var username = value.Trim().TrimStart('@');
        return username.Length is >= 5 and <= 32
            ? username
            : null;
    }
}
