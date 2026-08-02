using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Orders.GetOrder;

public sealed class GetOrderQueryHandler
    : IRequestHandler<GetOrderQuery, GetOrderResponse?>
{
    private readonly IOrderRepository _orderRepository;

    public GetOrderQueryHandler(IOrderRepository orderRepository)
    {
        _orderRepository = orderRepository;
    }

    public async Task<GetOrderResponse?> Handle(
        GetOrderQuery request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(request.Id, cancellationToken);

        if (order is null || order.Customer is null)
            return null;

        return new GetOrderResponse(
            order.Id,
            order.OrderNumber,
            order.Status.ToString(),
            order.RegisteredAt,
            order.PerfumeTotal,
            order.BottleTotal,
            order.FinalAmount,
            order.Notes,
            new OrderCustomerResponse(
                order.Customer.Id,
                order.Customer.FullName,
                order.Customer.Mobile,
                order.Customer.TelegramId),
            order.Items
                .OrderBy(item => item.RowNumber)
                .Select(item => new GetOrderItemResponse(
                    item.Id,
                    item.SalesListId,
                    item.Perfume?.Name ?? string.Empty,
                    item.Perfume?.Brand ?? string.Empty,
                    item.RequestedVolumeMl,
                    item.PerfumePricePerMl,
                    item.PerfumeAmount,
                    item.IsBottleOwner,
                    item.Bottle?.Name,
                    item.BottlePrice,
                    item.LineTotal,
                    item.RowNumber,
                    item.Notes))
                .ToArray());
    }
}
