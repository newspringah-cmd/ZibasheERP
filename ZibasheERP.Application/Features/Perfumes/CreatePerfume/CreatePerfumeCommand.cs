using MediatR;

namespace ZibasheERP.Application.Features.Perfumes.CreatePerfume;

public sealed record CreatePerfumeCommand(
    string Name,
    string EnglishName,
    string Brand,
    decimal PricePerMl,
    int OriginalBottleVolumeMl,
    string? Notes) : IRequest<PerfumeResponse>;

public sealed record PerfumeResponse(
    Guid Id,
    string Name,
    string EnglishName,
    string Brand,
    decimal PricePerMl,
    int OriginalBottleVolumeMl,
    bool IsActive,
    string? Notes);
