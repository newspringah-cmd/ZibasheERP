using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Orders.GetAdminOrders;

public sealed class GetAdminOrdersQueryHandler
    : IRequestHandler<GetAdminOrdersQuery, IReadOnlyCollection<AdminOrderSummary>>
{
    private readonly IOrderRepository _orderRepository;

    public GetAdminOrdersQueryHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<IReadOnlyCollection<AdminOrderSummary>> Handle(
        GetAdminOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var orders = await _orderRepository.GetForAdminAsync(
            request.Status,
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return orders
            .Where(order => order.Customer is not null)
            .Select(order => new AdminOrderSummary(
                order.Id,
                order.OrderNumber,
                order.Status.ToString(),
                order.RegisteredAt,
                order.CustomerId,
                order.Customer!.FullName,
                order.Customer.Mobile,
                order.Customer.TelegramId,
                order.FinalAmount,
                order.Items.Sum(item => item.RequestedVolumeMl),
                order.Items.Count))
            .ToArray();
    }
}
