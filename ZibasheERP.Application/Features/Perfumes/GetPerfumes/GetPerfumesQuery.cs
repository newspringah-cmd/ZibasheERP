using MediatR;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;

namespace ZibasheERP.Application.Features.Perfumes.GetPerfumes;

public sealed record GetPerfumesQuery(bool IncludeInactive = false, int Limit = 100)
    : IRequest<IReadOnlyCollection<PerfumeResponse>>;
