using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.AdvanceFulfillment;

public sealed class AdvanceFulfillmentCommandHandler
    : IRequestHandler<AdvanceFulfillmentCommand, AdvanceFulfillmentResponse>
{
    private readonly IOrderRepository _orderRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public AdvanceFulfillmentCommandHandler(
        IOrderRepository orderRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _orderRepository = orderRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<AdvanceFulfillmentResponse> Handle(
        AdvanceFulfillmentCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetForUpdateAsync(request.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");

        var allowed =
            order.Status == OrderStatus.Paid && request.TargetStatus == OrderStatus.Decanted ||
            order.Status == OrderStatus.Decanted && request.TargetStatus == OrderStatus.ReadyToShip;

        if (!allowed)
        {
            throw new InvalidOperationException(
                $"تغییر وضعیت از {order.Status} به {request.TargetStatus} مجاز نیست.");
        }

        var previous = order.Status;
        var now = DateTime.UtcNow;
        order.Status = request.TargetStatus;
        order.UpdatedAt = now;
        var eventType = request.TargetStatus switch
        {
            OrderStatus.Decanted => "OrderDecanted",
            OrderStatus.ReadyToShip => "OrderReadyToShip",
            _ => null
        };
        if (eventType is not null)
        {
            var notification = TelegramNotificationFactory.Create(
                order,
                eventType,
                new { order.Id, order.OrderNumber, Status = request.TargetStatus.ToString() },
                now);
            if (notification is not null)
                await _outboxRepository.AddAsync(notification, cancellationToken);
        }
        await _orderRepository.SaveChangesAsync(cancellationToken);

        return new AdvanceFulfillmentResponse(
            order.Id,
            previous.ToString(),
            order.Status.ToString(),
            now);
    }
}
