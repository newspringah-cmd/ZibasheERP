using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Bottles.GetAvailableBottles;

public sealed class GetAvailableBottlesQueryHandler
    : IRequestHandler<GetAvailableBottlesQuery, IReadOnlyCollection<AvailableBottleResponse>>
{
    private readonly IBottleRepository _bottleRepository;

    public GetAvailableBottlesQueryHandler(IBottleRepository bottleRepository)
    {
        _bottleRepository = bottleRepository;
    }

    public async Task<IReadOnlyCollection<AvailableBottleResponse>> Handle(
        GetAvailableBottlesQuery request,
        CancellationToken cancellationToken)
    {
        var bottles = await _bottleRepository.GetActiveAsync(cancellationToken);
        return bottles
            .Where(bottle =>
                bottle.VolumeMl == request.VolumeMl &&
                (request.VolumeMl != 3 || bottle.Type == BottleType.Normal) &&
                (request.VolumeMl <= 10 || bottle.Type == BottleType.Fancy))
            .Select(bottle => new AvailableBottleResponse(
                bottle.Id,
                bottle.Name,
                bottle.VolumeMl,
                bottle.Type.ToString(),
                bottle.SalePrice))
            .ToArray();
    }
}
