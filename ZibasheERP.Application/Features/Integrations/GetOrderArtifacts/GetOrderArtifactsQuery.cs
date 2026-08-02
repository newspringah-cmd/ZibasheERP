using MediatR;

namespace ZibasheERP.Application.Features.Integrations.GetOrderArtifacts;

public sealed record GetOrderArtifactsQuery(
    Guid OrderId,
    string? TelegramId = null) : IRequest<IReadOnlyCollection<OrderArtifactItem>>;

public sealed record OrderArtifactItem(
    Guid Id,
    string Type,
    string? FileUrl,
    string? ExternalFileId,
    string? ContentType,
    DateTime DeliveredAt);
