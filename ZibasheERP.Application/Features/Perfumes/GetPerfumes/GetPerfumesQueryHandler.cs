using MediatR;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Perfumes.GetPerfumes;

public sealed class GetPerfumesQueryHandler
    : IRequestHandler<GetPerfumesQuery, IReadOnlyCollection<PerfumeResponse>>
{
    private readonly IPerfumeRepository _repository;

    public GetPerfumesQueryHandler(IPerfumeRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyCollection<PerfumeResponse>> Handle(
        GetPerfumesQuery request,
        CancellationToken cancellationToken)
    {
        var perfumes = await _repository.GetAllAsync(
            request.IncludeInactive,
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return perfumes.Select(CreatePerfumeCommandHandler.ToResponse).ToArray();
    }
}
