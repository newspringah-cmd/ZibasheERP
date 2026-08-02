using MediatR;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Bottles.ManageBottles;

public sealed record CreateBottleCommand(
    string Name,
    int VolumeMl,
    BottleType Type,
    decimal SalePrice,
    bool IsDefault,
    string? Notes) : IRequest<AdminBottleResponse>;

public sealed record SetBottleStatusCommand(Guid BottleId, bool IsActive)
    : IRequest<AdminBottleResponse>;

public sealed record GetAdminBottlesQuery(bool IncludeInactive = false, int Limit = 100)
    : IRequest<IReadOnlyCollection<AdminBottleResponse>>;

public sealed record AdminBottleResponse(
    Guid Id,
    string Name,
    int VolumeMl,
    string Type,
    decimal SalePrice,
    bool IsDefault,
    bool IsActive,
    string? Notes);
