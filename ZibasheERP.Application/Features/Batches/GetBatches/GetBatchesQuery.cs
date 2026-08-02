using MediatR;
using ZibasheERP.Application.Features.Batches.CreateBatch;

namespace ZibasheERP.Application.Features.Batches.GetBatches;

public sealed record GetBatchesQuery(int Limit = 100)
    : IRequest<IReadOnlyCollection<BatchResponse>>;
