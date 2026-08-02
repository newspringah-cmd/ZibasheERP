using MediatR;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.GetAdminOrders;

public sealed record GetAdminOrdersQuery(OrderStatus? Status, int Limit = 50)
    : IRequest<IReadOnlyCollection<AdminOrderSummary>>;

public sealed record AdminOrderSummary(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTime RegisteredAt,
    Guid CustomerId,
    string CustomerName,
    string Mobile,
    string? TelegramId,
    decimal FinalAmount,
    int TotalVolumeMl,
    int ItemCount);
