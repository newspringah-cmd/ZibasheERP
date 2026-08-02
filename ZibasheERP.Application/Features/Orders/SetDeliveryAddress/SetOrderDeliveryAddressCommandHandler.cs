using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.SetDeliveryAddress;

public sealed class SetOrderDeliveryAddressCommandHandler
    : IRequestHandler<SetOrderDeliveryAddressCommand, SetOrderDeliveryAddressResponse>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IAddressRepository _addressRepository;

    public SetOrderDeliveryAddressCommandHandler(
        IOrderRepository orderRepository,
        IAddressRepository addressRepository)
    {
        _orderRepository = orderRepository;
        _addressRepository = addressRepository;
    }

    public async Task<SetOrderDeliveryAddressResponse> Handle(
        SetOrderDeliveryAddressCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetForUpdateAsync(request.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Shipped or OrderStatus.Delivered)
            throw new InvalidOperationException("در وضعیت فعلی سفارش امکان تغییر آدرس وجود ندارد.");

        var address = await _addressRepository.GetByIdAsync(request.AddressId, cancellationToken)
            ?? throw new InvalidOperationException("آدرس پیدا نشد.");
        if (address.CustomerId != order.CustomerId)
            throw new InvalidOperationException("آدرس متعلق به مشتری این سفارش نیست.");

        order.DeliveryAddressId = address.Id;
        order.UpdatedAt = DateTime.UtcNow;
        await _orderRepository.SaveChangesAsync(cancellationToken);
        return new(order.Id, address.Id, address.City, address.FullAddress);
    }
}
