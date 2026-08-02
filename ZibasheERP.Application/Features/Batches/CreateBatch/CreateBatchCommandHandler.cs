using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Batches.CreateBatch;

public sealed class CreateBatchCommandHandler
    : IRequestHandler<CreateBatchCommand, BatchResponse>
{
    private readonly IBatchRepository _batchRepository;
    private readonly IPerfumeRepository _perfumeRepository;

    public CreateBatchCommandHandler(
        IBatchRepository batchRepository,
        IPerfumeRepository perfumeRepository)
    {
        _batchRepository = batchRepository;
        _perfumeRepository = perfumeRepository;
    }

    public async Task<BatchResponse> Handle(
        CreateBatchCommand request,
        CancellationToken cancellationToken)
    {
        var perfume = await _perfumeRepository.GetByIdAsync(request.PerfumeId, cancellationToken)
            ?? throw new InvalidOperationException("عطر انتخاب‌شده پیدا نشد.");
        if (!perfume.IsActive)
            throw new InvalidOperationException("برای عطر غیرفعال نمی‌توان بچ جدید ثبت کرد.");

        var batchNumber = request.BatchNumber.Trim();
        if (await _batchRepository.BatchNumberExistsAsync(batchNumber, cancellationToken))
            throw new InvalidOperationException("شماره بچ قبلاً ثبت شده است.");

        var batch = new Batch
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            PerfumeId = perfume.Id,
            Perfume = perfume,
            BatchNumber = batchNumber,
            PurchasePrice = request.PurchasePrice,
            TotalVolumeMl = request.TotalVolumeMl,
            RemainingVolumeMl = request.TotalVolumeMl,
            PurchaseDate = request.PurchaseDate,
            Status = request.Status.Trim()
        };

        await _batchRepository.AddAsync(batch, cancellationToken);
        await _batchRepository.SaveChangesAsync(cancellationToken);
        return ToResponse(batch);
    }

    internal static BatchResponse ToResponse(Batch batch) => new(
        batch.Id,
        batch.PerfumeId,
        batch.Perfume.Name,
        batch.Perfume.Brand,
        batch.BatchNumber,
        batch.PurchasePrice,
        batch.TotalVolumeMl,
        batch.RemainingVolumeMl,
        batch.PurchaseDate,
        batch.Status);
}
