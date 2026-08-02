using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Shipments.GetShipmentTracking;

public sealed class GetShipmentTrackingQueryHandler
    : IRequestHandler<GetShipmentTrackingQuery, ShipmentTrackingResponse?>
{
    private readonly IOrderRepository _orderRepository;
    public GetShipmentTrackingQueryHandler(IOrderRepository orderRepository) => _orderRepository = orderRepository;

    public async Task<ShipmentTrackingResponse?> Handle(
        GetShipmentTrackingQuery request,
        CancellationToken cancellationToken)
    {
        var hasCustomerId = request.CustomerId.HasValue && request.CustomerId.Value != Guid.Empty;
        var hasTelegramId = !string.IsNullOrWhiteSpace(request.TelegramId);
        if (hasCustomerId == hasTelegramId)
            throw new InvalidOperationException("دقیقاً یک شناسه مشتری یا تلگرام باید ارسال شود.");

        var orderNumber = request.OrderNumber?.Trim();
        if (string.IsNullOrWhiteSpace(orderNumber) || orderNumber.Length > 100)
            throw new InvalidOperationException("شماره سفارش معتبر نیست.");
        var order = await _orderRepository.GetByOrderNumberAsync(orderNumber, cancellationToken);
        if (order?.Customer is null)
            return null;
        var ownsOrder = hasCustomerId
            ? order.CustomerId == request.CustomerId
            : string.Equals(order.Customer.TelegramId, request.TelegramId!.Trim(), StringComparison.Ordinal);
        if (!ownsOrder)
            return null;

        var shipment = order.Shipments
            .Where(item => !item.IsDeleted)
            .OrderByDescending(item => item.SentAt ?? item.RequestedAt ?? item.CreatedAt)
            .FirstOrDefault();
        return new ShipmentTrackingResponse(
            order.Id,
            order.OrderNumber,
            order.Status.ToString(),
            shipment?.ShippingCompany,
            shipment?.TrackingCode,
            shipment?.RequestedAt,
            shipment?.SentAt,
            shipment?.DeliveredAt,
            shipment?.IsDelivered ?? false);
    }
}
