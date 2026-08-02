using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Shipments.MarkDelivered;

public sealed class MarkShipmentDeliveredCommandHandler
    : IRequestHandler<MarkShipmentDeliveredCommand, MarkShipmentDeliveredResponse>
{
    private readonly IShipmentRepository _shipmentRepository;

    public MarkShipmentDeliveredCommandHandler(IShipmentRepository shipmentRepository)
    {
        _shipmentRepository = shipmentRepository;
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
