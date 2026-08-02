using MediatR;

namespace ZibasheERP.Application.Features.Shipments.GetShipmentTracking;

public sealed record GetShipmentTrackingQuery(
    string OrderNumber,
    Guid? CustomerId,
    string? TelegramId) : IRequest<ShipmentTrackingResponse?>;

public sealed record ShipmentTrackingResponse(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string? ShippingCompany,
    string? TrackingCode,
    DateTime? RequestedAt,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    bool IsDelivered);
