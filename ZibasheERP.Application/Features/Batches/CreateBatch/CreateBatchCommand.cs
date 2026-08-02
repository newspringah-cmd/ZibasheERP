using MediatR;

namespace ZibasheERP.Application.Features.Batches.CreateBatch;

public sealed record CreateBatchCommand(
    Guid PerfumeId,
    string BatchNumber,
    decimal PurchasePrice,
    decimal TotalVolumeMl,
    DateTime PurchaseDate,
    string Status = "Open") : IRequest<BatchResponse>;

public sealed record BatchResponse(
    Guid Id,
    Guid PerfumeId,
    string PerfumeName,
    string Brand,
    string BatchNumber,
    decimal PurchasePrice,
    decimal TotalVolumeMl,
    decimal RemainingVolumeMl,
    DateTime PurchaseDate,
    string Status);
