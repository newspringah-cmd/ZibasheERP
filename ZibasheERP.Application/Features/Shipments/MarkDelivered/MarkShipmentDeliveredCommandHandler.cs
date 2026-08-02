using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Shipments.MarkDelivered;

public sealed class MarkShipmentDeliveredCommandHandler
    : IRequestHandler<MarkShipmentDeliveredCommand, MarkShipmentDeliveredResponse>
{
    private readonly IShipmentRepository _shipmentRepository;
    private readonly INotificationOutboxRepository _notificationOutboxRepository;

    public MarkShipmentDeliveredCommandHandler(
        IShipmentRepository shipmentRepository,
        INotificationOutboxRepository notificationOutboxRepository)
    {
        _shipmentRepository = shipmentRepository;
        _notificationOutboxRepository = notificationOutboxRepository;
    }

    public async Task<MarkShipmentDeliveredResponse> Handle(
        MarkShipmentDeliveredCommand request,
        CancellationToken cancellationToken)
    {
        var shipment = await _shipmentRepository.GetByIdAsync(
            request.ShipmentId,
            cancellationToken)
            ?? throw new InvalidOperationException("مرسوله پیدا نشد.");
        var order = shipment.Order
            ?? throw new InvalidOperationException("سفارش مرتبط با مرسوله پیدا نشد.");

        if (shipment.IsDelivered && shipment.DeliveredAt.HasValue)
            return BuildResponse(shipment, order, shipment.DeliveredAt.Value);

        if (!shipment.SentAt.HasValue || order.Status != OrderStatus.Shipped)
            throw new InvalidOperationException("فقط مرسوله ارسال‌شده قابل ثبت تحویل است.");

        var now = DateTime.UtcNow;
        shipment.IsDelivered = true;
        shipment.DeliveredAt = now;
        shipment.UpdatedAt = now;
        order.Status = OrderStatus.Delivered;
        order.UpdatedAt = now;

        var notification = TelegramNotificationFactory.Create(
            order,
            "OrderDelivered",
            new { order.Id, order.OrderNumber, Status = OrderStatus.Delivered.ToString() },
            now);
        if (notification is not null)
            await _notificationOutboxRepository.AddAsync(notification, cancellationToken);
        await _shipmentRepository.SaveChangesAsync(cancellationToken);
        return BuildResponse(shipment, order, now);
    }

    private static MarkShipmentDeliveredResponse BuildResponse(
        Shipment shipment,
        Order order,
        DateTime deliveredAt) => new(
            shipment.Id,
            order.Id,
            order.Status.ToString(),
            deliveredAt);
}
