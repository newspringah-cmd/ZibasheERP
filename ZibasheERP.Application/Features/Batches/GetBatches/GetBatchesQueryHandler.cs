using MediatR;
using ZibasheERP.Application.Features.Batches.CreateBatch;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Batches.GetBatches;

public sealed class GetBatchesQueryHandler
    : IRequestHandler<GetBatchesQuery, IReadOnlyCollection<BatchResponse>>
{
    private readonly IBatchRepository _repository;

    public GetBatchesQueryHandler(IBatchRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyCollection<BatchResponse>> Handle(
        GetBatchesQuery request,
        CancellationToken cancellationToken)
    {
        var batches = await _repository.GetForInventoryAsync(
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return batches.Select(CreateBatchCommandHandler.ToResponse).ToArray();
    }
}
