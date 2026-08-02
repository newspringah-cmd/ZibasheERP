using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Shipments.CreateShipment;

public sealed class CreateShipmentCommandHandler
    : IRequestHandler<CreateShipmentCommand, CreateShipmentResponse>
{
    private readonly IShipmentRepository _shipmentRepository;
    private readonly INotificationOutboxRepository _notificationOutboxRepository;

    public CreateShipmentCommandHandler(
        IShipmentRepository shipmentRepository,
        INotificationOutboxRepository notificationOutboxRepository)
    {
        _shipmentRepository = shipmentRepository;
        _notificationOutboxRepository = notificationOutboxRepository;
    }

    public async Task<CreateShipmentResponse> Handle(
        CreateShipmentCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _shipmentRepository.GetOrderForShippingAsync(
            request.OrderId,
            cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");

        if (order.Status != OrderStatus.ReadyToShip)
            throw new InvalidOperationException("فقط سفارش آماده ارسال قابل ثبت مرسوله است.");

        if (order.Shipments.Any(shipment => !shipment.IsDeleted))
            throw new InvalidOperationException("برای این سفارش قبلاً مرسوله ثبت شده است.");

        var address = await _shipmentRepository.GetAddressAsync(
            request.AddressId,
            cancellationToken)
            ?? throw new InvalidOperationException("آدرس پیدا نشد.");

        if (address.CustomerId != order.CustomerId)
            throw new InvalidOperationException("آدرس انتخاب‌شده متعلق به مشتری سفارش نیست.");

        if (order.DeliveryAddressId.HasValue && order.DeliveryAddressId != address.Id)
            throw new InvalidOperationException("آدرس مرسوله با آدرس انتخاب‌شده مشتری یکسان نیست.");

        var trackingCode = request.TrackingCode.Trim();
        if (await _shipmentRepository.TrackingCodeExistsAsync(trackingCode, cancellationToken))
            throw new InvalidOperationException("این کد رهگیری قبلاً ثبت شده است.");

        var now = DateTime.UtcNow;
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            OrderId = order.Id,
            AddressId = address.Id,
            ReceiverName = address.ReceiverName,
            Mobile = address.Mobile,
            Province = address.Province,
            City = address.City,
            PostalCode = address.PostalCode,
            FullAddress = address.FullAddress,
            ShippingCompany = request.ShippingCompany.Trim(),
            ShippingCost = request.ShippingCost,
            TrackingCode = trackingCode,
            RequestedAt = now,
            SentAt = now,
            Notes = NormalizeOptional(request.Notes)
        };

        order.Status = OrderStatus.Shipped;
        order.ShippedAt = now;
        order.UpdatedAt = now;

        await _shipmentRepository.AddAsync(shipment, cancellationToken);
        var notification = TelegramNotificationFactory.Create(
            order,
            "OrderShipped",
            new
            {
                order.Id,
                order.OrderNumber,
                Status = OrderStatus.Shipped.ToString(),
                shipment.ShippingCompany,
                shipment.TrackingCode
            },
            now);
        if (notification is not null)
            await _notificationOutboxRepository.AddAsync(notification, cancellationToken);
        await _shipmentRepository.SaveChangesAsync(cancellationToken);

        return new CreateShipmentResponse(
            shipment.Id,
            order.Id,
            order.Status.ToString(),
            shipment.ShippingCompany,
            trackingCode,
            now);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
