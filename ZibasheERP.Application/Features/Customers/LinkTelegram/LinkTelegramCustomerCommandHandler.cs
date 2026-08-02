using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Customers.LinkTelegram;

public sealed class LinkTelegramCustomerCommandHandler
    : IRequestHandler<LinkTelegramCustomerCommand, LinkTelegramCustomerResult>
{
    private readonly ICustomerRepository _customerRepository;

    public LinkTelegramCustomerCommandHandler(ICustomerRepository customerRepository)
    {
        _customerRepository = customerRepository;
    }

    public async Task<LinkTelegramCustomerResult> Handle(
        LinkTelegramCustomerCommand request,
        CancellationToken cancellationToken)
    {
        var telegramId = request.TelegramId.Trim();
        var mobile = IranianMobileNormalizer.Normalize(request.Mobile);
        if (mobile is null)
            return new(LinkTelegramCustomerStatus.InvalidMobile);

        var telegramCustomer = await _customerRepository.GetByTelegramIdAsync(
            telegramId,
            cancellationToken);
        if (telegramCustomer is not null)
        {
            return telegramCustomer.Mobile == mobile
                ? new(LinkTelegramCustomerStatus.AlreadyLinked, telegramCustomer.FullName)
                : new(LinkTelegramCustomerStatus.TelegramAlreadyLinked);
        }

        var customer = await _customerRepository.GetByMobileAsync(mobile, cancellationToken);
        if (customer is null)
            return new(LinkTelegramCustomerStatus.CustomerNotFound);

        if (!string.IsNullOrWhiteSpace(customer.TelegramId) && customer.TelegramId != telegramId)
            return new(LinkTelegramCustomerStatus.CustomerLinkedToAnotherTelegram);

        customer.TelegramId = telegramId;
        customer.Username = string.IsNullOrWhiteSpace(request.Username)
            ? customer.Username
            : request.Username.Trim();
        customer.UpdatedAt = DateTime.UtcNow;
        await _customerRepository.UpdateAsync(customer, cancellationToken);
        await _customerRepository.SaveChangesAsync(cancellationToken);

        return new(LinkTelegramCustomerStatus.Linked, customer.FullName);
    }
}
