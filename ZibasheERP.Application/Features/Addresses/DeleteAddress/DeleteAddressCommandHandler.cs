using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Addresses.DeleteAddress;

public sealed class DeleteAddressCommandHandler : IRequestHandler<DeleteAddressCommand>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IAddressRepository _addressRepository;
    private readonly IOrderRepository _orderRepository;

    public DeleteAddressCommandHandler(
        ICustomerRepository customerRepository,
        IAddressRepository addressRepository,
        IOrderRepository orderRepository)
    {
        _customerRepository = customerRepository;
        _addressRepository = addressRepository;
        _orderRepository = orderRepository;
    }

    public async Task Handle(DeleteAddressCommand request, CancellationToken cancellationToken)
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

        var orders = await _orderRepository.GetByCustomerIdAsync(customer.Id, cancellationToken);
        if (orders.Any(order =>
                order.DeliveryAddressId == selected.Id &&
                order.Status is not (OrderStatus.Cancelled or OrderStatus.Delivered)))
        {
            throw new InvalidOperationException("این آدرس به یک سفارش فعال متصل است و قابل حذف نیست.");
        }

        var now = DateTime.UtcNow;
        selected.IsDeleted = true;
        selected.IsDefault = false;
        selected.UpdatedAt = now;
        if (!addresses.Any(address => address.Id != selected.Id && address.IsDefault))
        {
            var replacement = addresses.FirstOrDefault(address => address.Id != selected.Id);
            if (replacement is not null)
            {
                replacement.IsDefault = true;
                replacement.UpdatedAt = now;
            }
        }

        await _addressRepository.SaveChangesAsync(cancellationToken);
    }
}
