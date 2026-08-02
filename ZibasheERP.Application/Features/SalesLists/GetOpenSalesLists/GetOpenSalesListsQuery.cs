using MediatR;

namespace ZibasheERP.Application.Features.SalesLists.GetOpenSalesLists;

public sealed record GetOpenSalesListsQuery(int Limit = 10)
    : IRequest<IReadOnlyCollection<OpenSalesListResponse>>;

public sealed record OpenSalesListResponse(
    Guid Id,
    string PerfumeName,
    string EnglishName,
    string Brand,
    decimal PricePerMl,
    int RemainingVolumeMl,
    bool BottleOwnerAvailable,
    DateTime OpenDate);
