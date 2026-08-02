using MediatR;

namespace ZibasheERP.Application.Features.Shipments.CreateShipment;

public sealed record CreateShipmentCommand(
    Guid OrderId,
    Guid AddressId,
    string ShippingCompany,
    decimal ShippingCost,
    string TrackingCode,
    string? Notes) : IRequest<CreateShipmentResponse>;

public sealed record CreateShipmentResponse(
    Guid ShipmentId,
    Guid OrderId,
    string OrderStatus,
    string ShippingCompany,
    string TrackingCode,
    DateTime SentAt);
