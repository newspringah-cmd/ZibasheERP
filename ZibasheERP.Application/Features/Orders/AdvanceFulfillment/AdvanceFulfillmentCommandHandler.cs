using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.AdvanceFulfillment;

public sealed class AdvanceFulfillmentCommandHandler
    : IRequestHandler<AdvanceFulfillmentCommand, AdvanceFulfillmentResponse>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public AdvanceFulfillmentCommandHandler(
        IOrderRepository orderRepository,
        IBatchRepository batchRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _orderRepository = orderRepository;
        _batchRepository = batchRepository;
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
        if (request.TargetStatus == OrderStatus.Decanted)
        {
            var batchId = order.SalesList?.BatchId
                ?? throw new InvalidOperationException("بچ سفارش بارگذاری نشده است.");
            var batch = await _batchRepository.GetByIdAsync(batchId, cancellationToken)
                ?? throw new InvalidOperationException("بچ سفارش پیدا نشد.");
            var volume = order.Items.Where(item => !item.IsDeleted)
                .Sum(item => item.RequestedVolumeMl);
            if (volume <= 0)
                throw new InvalidOperationException("حجم سفارش برای کسر از موجودی معتبر نیست.");
            if (batch.RemainingVolumeMl < volume)
                throw new InvalidOperationException("موجودی بچ برای انجام دکانت کافی نیست.");

            batch.RemainingVolumeMl -= volume;
            batch.Status = batch.RemainingVolumeMl == 0 ? "Depleted" : batch.Status;
            batch.UpdatedAt = now;
            await _batchRepository.UpdateAsync(batch, cancellationToken);
        }
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
            if (request.TargetStatus == OrderStatus.Decanted)
            {
                var integrationEvent = N8nIntegrationEventFactory.Create(
                    order,
                    "OrderDecanted",
                    new
                    {
                        order.Id,
                        order.OrderNumber,
                        Customer = new
                        {
                            order.CustomerId,
                            order.Customer?.FullName,
                            order.Customer?.TelegramId
                        },
                        Items = order.Items
                            .Where(item => !item.IsDeleted)
                            .Select(item => new
                            {
                                item.Id,
                                item.PerfumeId,
                                PerfumeName = item.Perfume?.Name,
                                item.RequestedVolumeMl,
                                item.BottleId,
                                BottleName = item.Bottle?.Name
                            })
                    },
                    now);
                await _outboxRepository.AddAsync(integrationEvent, cancellationToken);
            }
        }
        await _orderRepository.SaveChangesAsync(cancellationToken);

        return new AdvanceFulfillmentResponse(
            order.Id,
            previous.ToString(),
            order.Status.ToString(),
            now);
    }
}
