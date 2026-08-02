using MediatR;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;

namespace ZibasheERP.Application.Features.Perfumes.ManagePerfume;

public sealed record SetPerfumeStatusCommand(Guid PerfumeId, bool IsActive)
    : IRequest<PerfumeResponse>;

public sealed record UpdatePerfumePriceCommand(Guid PerfumeId, decimal PricePerMl)
    : IRequest<PerfumeResponse>;
