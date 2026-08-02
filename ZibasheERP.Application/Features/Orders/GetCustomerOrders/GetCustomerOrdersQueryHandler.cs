using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.GetCustomerOrders;

public sealed class GetCustomerOrdersQueryHandler
    : IRequestHandler<GetCustomerOrdersQuery, IReadOnlyCollection<CustomerOrderSummary>>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IOrderRepository _orderRepository;

    public GetCustomerOrdersQueryHandler(
        ICustomerRepository customerRepository,
        IOrderRepository orderRepository)
    {
        _customerRepository = customerRepository;
        _orderRepository = orderRepository;
    }

    public async Task<IReadOnlyCollection<CustomerOrderSummary>> Handle(
        GetCustomerOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var customer = await ResolveCustomerAsync(request, cancellationToken);

        if (customer is null)
            return Array.Empty<CustomerOrderSummary>();

        var orders = await _orderRepository.GetByCustomerIdAsync(
            customer.Id,
            cancellationToken);

        return orders
            .Select(order => new CustomerOrderSummary(
                order.Id,
                order.OrderNumber,
                order.Status.ToString(),
                order.RegisteredAt,
                order.FinalAmount,
                order.Items.Sum(item => item.RequestedVolumeMl),
                order.Items.Count))
            .ToArray();
    }

    private async Task<Customer?> ResolveCustomerAsync(
        GetCustomerOrdersQuery request,
        CancellationToken cancellationToken)
    {
        if (request.CustomerId is { } customerId && customerId != Guid.Empty)
        {
            return await _customerRepository.GetByIdAsync(
                customerId,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.TelegramId))
        {
            return await _customerRepository.GetByTelegramIdAsync(
                request.TelegramId.Trim(),
                cancellationToken);
        }

        return null;
    }
}
