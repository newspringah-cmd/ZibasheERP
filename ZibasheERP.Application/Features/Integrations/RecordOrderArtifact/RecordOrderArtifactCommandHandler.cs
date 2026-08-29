using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Integrations.RecordOrderArtifact;

public sealed class RecordOrderArtifactCommandHandler
    : IRequestHandler<RecordOrderArtifactCommand, OrderArtifactResponse>
{
    private readonly IOrderArtifactRepository _artifactRepository;
    private readonly INotificationOutboxRepository _outboxRepository;
    private readonly IInvoiceRepository _invoiceRepository;

    public RecordOrderArtifactCommandHandler(
        IOrderArtifactRepository artifactRepository,
        INotificationOutboxRepository outboxRepository,
        IInvoiceRepository invoiceRepository)
    {
        _artifactRepository = artifactRepository;
        _outboxRepository = outboxRepository;
        _invoiceRepository = invoiceRepository;
    }

    public async Task<OrderArtifactResponse> Handle(
        RecordOrderArtifactCommand request,
        CancellationToken cancellationToken)
    {
        var existing = await _artifactRepository.GetBySourceEventIdAsync(
            request.SourceEventId,
            cancellationToken);
        if (existing is not null)
            return Map(existing);

        var sourceEvent = await _outboxRepository.GetByIdAsync(
            request.SourceEventId,
            cancellationToken);
        if (sourceEvent is null ||
            sourceEvent.Channel != "N8n" ||
            sourceEvent.OrderId != request.OrderId ||
            sourceEvent.EventType != ExpectedEventType(request.Type))
        {
            throw new InvalidOperationException("رویداد منبع برای این فایل معتبر نیست.");
        }

        var fileUrl = NormalizeOptional(request.FileUrl, 2000, "آدرس فایل");
        var externalFileId = NormalizeOptional(request.ExternalFileId, 250, "شناسه خارجی فایل");
        if (fileUrl is null && externalFileId is null)
            throw new InvalidOperationException("حداقل آدرس فایل یا شناسه خارجی فایل لازم است.");
        if (fileUrl is not null &&
            (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("https" or "http")))
        {
            throw new InvalidOperationException("آدرس فایل معتبر نیست.");
        }

        var now = DateTime.UtcNow;
        var artifact = new OrderArtifact
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            OrderId = request.OrderId,
            SourceEventId = request.SourceEventId,
            Type = request.Type,
            FileUrl = fileUrl,
            ExternalFileId = externalFileId,
            ContentType = NormalizeOptional(request.ContentType, 100, "نوع محتوا"),
            DeliveredAt = now
        };
        await _artifactRepository.AddAsync(artifact, cancellationToken);
        await _artifactRepository.SaveChangesAsync(cancellationToken);
        if (request.Type == OrderArtifactType.InvoicePdf)
        {
            var invoice = await _invoiceRepository.GetForUpdateByOrderIdAsync(request.OrderId, cancellationToken);
            if (invoice is not null)
            {
                invoice.IsSentToCustomer = true;
                invoice.SentToCustomerAt = now;
                invoice.DeliveryStatus = InvoiceDeliveryStatus.Delivered;
                invoice.DeliveryStatusChangedAt = now;
                invoice.DeliveryStatusNote = null;
                await _invoiceRepository.SaveChangesAsync(cancellationToken);
            }
        }
        return Map(artifact);
    }

    private static string ExpectedEventType(OrderArtifactType type) => type switch
    {
        OrderArtifactType.InvoicePdf => "InvoiceIssued",
        OrderArtifactType.DecantPhoto => "OrderDecanted",
        OrderArtifactType.PostalReceipt => "OrderShipped",
        _ => throw new InvalidOperationException("نوع فایل سفارش معتبر نیست.")
    };

    private static string? NormalizeOptional(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new InvalidOperationException($"{field} بیش از حد طولانی است.");
        return normalized;
    }

    private static OrderArtifactResponse Map(OrderArtifact artifact) => new(
        artifact.Id,
        artifact.OrderId,
        artifact.SourceEventId,
        artifact.Type.ToString(),
        artifact.FileUrl,
        artifact.ExternalFileId,
        artifact.ContentType,
        artifact.DeliveredAt);
}
