using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

public sealed class CreateSalesListCommandHandler
    : IRequestHandler<CreateSalesListCommand, AdminSalesListResponse>
{
    private readonly ISalesListRepository _salesListRepository;
    private readonly IPerfumeRepository _perfumeRepository;

    public CreateSalesListCommandHandler(
        ISalesListRepository salesListRepository,
        IPerfumeRepository perfumeRepository)
    {
        _salesListRepository = salesListRepository;
        _perfumeRepository = perfumeRepository;
    }

    public async Task<AdminSalesListResponse> Handle(
        CreateSalesListCommand request,
        CancellationToken cancellationToken)
    {
        var perfume = await _perfumeRepository.GetByIdAsync(request.PerfumeId, cancellationToken)
            ?? throw new InvalidOperationException("عطر انتخاب‌شده پیدا نشد.");
        if (!perfume.IsActive)
            throw new InvalidOperationException("عطر انتخاب‌شده غیرفعال است.");
        if (request.MinimumRequestVolumeMl <= 0 || request.MinimumRequestVolumeMl > request.TotalVolume)
            throw new InvalidOperationException("حداقل حجم درخواست معتبر نیست.");

        var now = DateTime.UtcNow;
        var publicCode = await GeneratePublicCodeAsync(cancellationToken);
        var salesList = new SalesList
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            PublicCode = publicCode,
            EnglishName = NormalizeRequired(request.EnglishName, perfume.EnglishName),
            ProductPageUrl = NormalizeOptional(request.ProductPageUrl) ?? string.Empty,
            DisplayBrand = NormalizeRequired(request.DisplayBrand, perfume.Brand),
            Gender = Enum.IsDefined(typeof(PerfumeGender), request.Gender)
                ? (PerfumeGender)request.Gender : PerfumeGender.Unisex,
            ReleaseYear = request.ReleaseYear,
            PersianName = NormalizeRequired(request.PersianName, perfume.Name),
            TopNotes = NormalizeOptional(request.TopNotes) ?? string.Empty,
            MiddleNotes = NormalizeOptional(request.MiddleNotes) ?? string.Empty,
            BaseNotes = NormalizeOptional(request.BaseNotes) ?? string.Empty,
            Accords = NormalizeOptional(request.Accords) ?? string.Empty,
            PerfumeId = perfume.Id,
            Perfume = perfume,
            BatchId = null,
            PricePerMl = request.PricePerMl,
            TotalVolume = request.TotalVolume,
            MinimumRequestVolumeMl = request.MinimumRequestVolumeMl,
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

    private static string NormalizeRequired(string? value, string fallback) =>
        NormalizeOptional(value) ?? fallback.Trim();

    private async Task<int> GeneratePublicCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = Random.Shared.Next(10000, 100000);
            if (!await _salesListRepository.PublicCodeExistsAsync(code, cancellationToken))
                return code;
        }
        throw new InvalidOperationException("تولید کد یکتای لیست ناموفق بود؛ دوباره تلاش کنید.");
    }
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
        salesList.Batch?.BatchNumber,
        salesList.Perfume.Name,
        salesList.Perfume.Brand,
        salesList.PricePerMl,
        salesList.TotalVolume,
        salesList.MinimumRequestVolumeMl,
        salesList.ReservedVolume,
        salesList.RemainingVolume,
        salesList.HasBottleOwner,
        salesList.BottleOwnerCustomer?.FullName,
        salesList.Status.ToString(),
        salesList.OpenDate,
        salesList.ClosedDate,
        salesList.TelegramChannelId,
        salesList.Notes,
        salesList.PublicCode);
}
