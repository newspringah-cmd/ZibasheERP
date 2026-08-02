using MediatR;

namespace ZibasheERP.Application.Features.Bottles.GetAvailableBottles;

public sealed record GetAvailableBottlesQuery(int VolumeMl)
    : IRequest<IReadOnlyCollection<AvailableBottleResponse>>;

public sealed record AvailableBottleResponse(
    Guid Id,
    string Name,
    int VolumeMl,
    string Type,
    decimal Price);
