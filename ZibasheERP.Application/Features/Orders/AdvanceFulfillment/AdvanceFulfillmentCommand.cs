using MediatR;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.AdvanceFulfillment;

public sealed record AdvanceFulfillmentCommand(
    Guid OrderId,
    OrderStatus TargetStatus) : IRequest<AdvanceFulfillmentResponse>;

public sealed record AdvanceFulfillmentResponse(
    Guid OrderId,
    string PreviousStatus,
    string CurrentStatus,
    DateTime UpdatedAt);
