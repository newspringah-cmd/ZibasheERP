using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

public sealed class CreateSalesListCommandHandler
    : IRequestHandler<CreateSalesListCommand, AdminSalesListResponse>
{
    private readonly ISalesListRepository _salesListRepository;
    private readonly IBatchRepository _batchRepository;

    public CreateSalesListCommandHandler(
        ISalesListRepository salesListRepository,
        IBatchRepository batchRepository)
    {
        _salesListRepository = salesListRepository;
        _batchRepository = batchRepository;
    }

    public async Task<AdminSalesListResponse> Handle(
        CreateSalesListCommand request,
        CancellationToken cancellationToken)
    {
        var batch = await _batchRepository.GetByIdAsync(request.BatchId, cancellationToken)
            ?? throw new InvalidOperationException("بچ انتخاب‌شده پیدا نشد.");
        if (!batch.Perfume.IsActive)
            throw new InvalidOperationException("عطر این بچ غیرفعال است.");
        if (request.TotalVolume > batch.RemainingVolumeMl)
            throw new InvalidOperationException("حجم لیست از موجودی باقیمانده بچ بیشتر است.");
        if (await _salesListRepository.HasActiveForBatchAsync(batch.Id, cancellationToken))
            throw new InvalidOperationException("برای این بچ یک لیست فروش فعال وجود دارد.");

        var now = DateTime.UtcNow;
        var salesList = new SalesList
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            BatchId = batch.Id,
            Batch = batch,
            PricePerMl = request.PricePerMl,
            TotalVolume = request.TotalVolume,
            ReservedVolume = 0,
            Status = SalesListStatus.Open,
            OpenDate = now,
            TelegramChannelId = NormalizeOptional(request.TelegramChannelId),
            Notes = NormalizeOptional(request.Notes)
        };

        await _salesListRepository.AddAsync(salesList, cancellationToken);
        await _salesListRepository.SaveChangesAsync(cancellationToken);
        return SalesListMapper.ToResponse(salesList);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CloseSalesListCommandHandler
    : IRequestHandler<CloseSalesListCommand, AdminSalesListResponse>
{
    private readonly ISalesListRepository _repository;

    public CloseSalesListCommandHandler(ISalesListRepository repository)
    {
        _repository = repository;
    }

    public async Task<AdminSalesListResponse> Handle(
        CloseSalesListCommand request,
        CancellationToken cancellationToken)
    {
        var salesList = await _repository.GetByIdAsync(request.SalesListId, cancellationToken)
            ?? throw new InvalidOperationException("لیست فروش پیدا نشد.");
        if (salesList.Status is SalesListStatus.Closed or SalesListStatus.Cancelled)
            return SalesListMapper.ToResponse(salesList);
        if (salesList.Status is SalesListStatus.Purchased or SalesListStatus.Invoiced)
            throw new InvalidOperationException("این لیست وارد فرآیند خرید یا فاکتور شده و قابل بستن نیست.");

        salesList.Status = SalesListStatus.Closed;
        salesList.ClosedDate = DateTime.UtcNow;
        salesList.UpdatedAt = salesList.ClosedDate;
        await _repository.UpdateAsync(salesList, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return SalesListMapper.ToResponse(salesList);
    }
}

public sealed class GetAdminSalesListsQueryHandler
    : IRequestHandler<GetAdminSalesListsQuery, IReadOnlyCollection<AdminSalesListResponse>>
{
    private readonly ISalesListRepository _repository;

    public GetAdminSalesListsQueryHandler(ISalesListRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyCollection<AdminSalesListResponse>> Handle(
        GetAdminSalesListsQuery request,
        CancellationToken cancellationToken)
    {
        var lists = await _repository.GetForAdminAsync(
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return lists.Select(SalesListMapper.ToResponse).ToArray();
    }
}

internal static class SalesListMapper
{
    internal static AdminSalesListResponse ToResponse(SalesList salesList) => new(
        salesList.Id,
        salesList.BatchId,
        salesList.Batch.BatchNumber,
        salesList.Batch.Perfume.Name,
        salesList.Batch.Perfume.Brand,
        salesList.PricePerMl,
        salesList.TotalVolume,
        salesList.ReservedVolume,
        salesList.RemainingVolume,
        salesList.HasBottleOwner,
        salesList.BottleOwnerCustomer?.FullName,
        salesList.Status.ToString(),
        salesList.OpenDate,
        salesList.ClosedDate,
        salesList.TelegramChannelId,
        salesList.Notes);
}
