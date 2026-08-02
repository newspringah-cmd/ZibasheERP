using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Integrations.GetOrderArtifacts;

public sealed class GetOrderArtifactsQueryHandler
    : IRequestHandler<GetOrderArtifactsQuery, IReadOnlyCollection<OrderArtifactItem>>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IOrderArtifactRepository _artifactRepository;

    public GetOrderArtifactsQueryHandler(
        IOrderRepository orderRepository,
        IOrderArtifactRepository artifactRepository)
    {
        _orderRepository = orderRepository;
        _artifactRepository = artifactRepository;
    }

    public async Task<IReadOnlyCollection<OrderArtifactItem>> Handle(
        GetOrderArtifactsQuery request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
            return Array.Empty<OrderArtifactItem>();
        if (!string.IsNullOrWhiteSpace(request.TelegramId) &&
            order.Customer?.TelegramId != request.TelegramId.Trim())
        {
            return Array.Empty<OrderArtifactItem>();
        }

        var artifacts = await _artifactRepository.GetByOrderIdAsync(order.Id, cancellationToken);
        return artifacts.Select(artifact => new OrderArtifactItem(
            artifact.Id,
            artifact.Type.ToString(),
            artifact.FileUrl,
            artifact.ExternalFileId,
            artifact.ContentType,
            artifact.DeliveredAt))
            .ToArray();
    }
}
