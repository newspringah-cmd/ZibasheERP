using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Addresses.SetDefaultAddress;

public sealed class SetDefaultAddressCommandHandler : IRequestHandler<SetDefaultAddressCommand>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IAddressRepository _addressRepository;

    public SetDefaultAddressCommandHandler(
        ICustomerRepository customerRepository,
        IAddressRepository addressRepository)
    {
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
    }

    public async Task Handle(SetDefaultAddressCommand request, CancellationToken cancellationToken)
    {
        var hasCustomerId = request.CustomerId is { } customerId && customerId != Guid.Empty;
        var hasTelegramId = !string.IsNullOrWhiteSpace(request.TelegramId);
        if (hasCustomerId == hasTelegramId)
            throw new InvalidOperationException("دقیقاً یک شناسه مشتری باید مشخص شود.");

        var customer = hasCustomerId
            ? await _customerRepository.GetByIdAsync(request.CustomerId!.Value, cancellationToken)
            : await _customerRepository.GetByTelegramIdAsync(request.TelegramId!.Trim(), cancellationToken);
        if (customer is null)
            throw new InvalidOperationException("حساب مشتری پیدا نشد.");

        var addresses = await _addressRepository.GetByCustomerIdAsync(customer.Id, cancellationToken);
        var selected = addresses.FirstOrDefault(address => address.Id == request.AddressId);
        if (selected is null)
            throw new InvalidOperationException("آدرس پیدا نشد یا متعلق به این مشتری نیست.");

        var now = DateTime.UtcNow;
        foreach (var address in addresses)
        {
            var shouldBeDefault = address.Id == selected.Id;
            if (address.IsDefault == shouldBeDefault)
                continue;
            address.IsDefault = shouldBeDefault;
            address.UpdatedAt = now;
        }

        await _addressRepository.SaveChangesAsync(cancellationToken);
    }
}
