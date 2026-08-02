using MediatR;

namespace ZibasheERP.Application.Features.Shipments.MarkDelivered;

public sealed record MarkShipmentDeliveredCommand(Guid ShipmentId)
    : IRequest<MarkShipmentDeliveredResponse>;

public sealed record MarkShipmentDeliveredResponse(
    Guid ShipmentId,
    Guid OrderId,
    string OrderStatus,
    DateTime DeliveredAt);
