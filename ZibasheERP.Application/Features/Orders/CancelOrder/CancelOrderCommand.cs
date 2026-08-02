using MediatR;

namespace ZibasheERP.Application.Features.Orders.CancelOrder;

public sealed record CancelOrderCommand(Guid OrderId, string Reason)
    : IRequest<CancelOrderResponse>;

public sealed record CancelOrderResponse(
    Guid OrderId,
    string Status,
    string Reason,
    DateTime CancelledAt);
