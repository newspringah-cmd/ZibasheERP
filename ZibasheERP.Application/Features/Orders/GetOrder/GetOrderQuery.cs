using MediatR;

namespace ZibasheERP.Application.Features.Orders.GetOrder;

public sealed record GetOrderQuery(Guid Id) : IRequest<GetOrderResponse?>;
