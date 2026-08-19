using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.SalesLists.GetOpenSalesLists;

public sealed class GetOpenSalesListsQueryHandler
    : IRequestHandler<GetOpenSalesListsQuery, IReadOnlyCollection<OpenSalesListResponse>>
{
    private readonly ISalesListRepository _salesListRepository;

    public GetOpenSalesListsQueryHandler(ISalesListRepository salesListRepository)
    {
        _salesListRepository = salesListRepository;
    }

    public async Task<IReadOnlyCollection<OpenSalesListResponse>> Handle(
        GetOpenSalesListsQuery request,
        CancellationToken cancellationToken)
    {
        var lists = await _salesListRepository.GetOpenAsync(
            Math.Clamp(request.Limit, 1, 50),
            cancellationToken);

        return lists.Select(salesList => new OpenSalesListResponse(
            salesList.Id,
            salesList.Perfume.Name,
            salesList.Perfume.EnglishName,
            salesList.Perfume.Brand,
            salesList.PricePerMl,
            salesList.RemainingVolume,
            !salesList.HasBottleOwner,
            salesList.OpenDate))
            .ToArray();
    }
}
