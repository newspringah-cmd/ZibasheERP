using MediatR;

namespace ZibasheERP.Application.Features.Orders.GetCustomerOrders;

public sealed record GetCustomerOrdersQuery(
    Guid? CustomerId,
    string? TelegramId) : IRequest<IReadOnlyCollection<CustomerOrderSummary>>;

public sealed record CustomerOrderSummary(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTime RegisteredAt,
    decimal FinalAmount,
    int TotalVolumeMl,
    int ItemCount);
