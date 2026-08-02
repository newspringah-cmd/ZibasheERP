using MediatR;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Integrations.RecordOrderArtifact;

public sealed record RecordOrderArtifactCommand(
    Guid SourceEventId,
    Guid OrderId,
    OrderArtifactType Type,
    string? FileUrl,
    string? ExternalFileId,
    string? ContentType) : IRequest<OrderArtifactResponse>;

public sealed record OrderArtifactResponse(
    Guid Id,
    Guid OrderId,
    Guid SourceEventId,
    string Type,
    string? FileUrl,
    string? ExternalFileId,
    string? ContentType,
    DateTime DeliveredAt);
