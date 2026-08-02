using ZibasheERP.Application.Features.Integrations.RecordOrderArtifact;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Integrations;

public sealed class RecordOrderArtifactCommandHandlerTests
{
    [Fact]
    public async Task Handle_ValidSourceEvent_StoresArtifactIdempotently()
    {
        var orderId = Guid.NewGuid();
        var source = new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Channel = "N8n",
            EventType = "InvoiceIssued"
        };
        var artifacts = new ArtifactRepositoryStub();
        var handler = new RecordOrderArtifactCommandHandler(
            artifacts,
            new OutboxRepositoryStub(source));
        var command = new RecordOrderArtifactCommand(
            source.Id,
            orderId,
            OrderArtifactType.InvoicePdf,
            "https://files.example.test/invoice.pdf",
            null,
            "application/pdf");

        var first = await handler.Handle(command, CancellationToken.None);
        var second = await handler.Handle(command, CancellationToken.None);

        Assert.Equal(OrderArtifactType.InvoicePdf.ToString(), first.Type);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, artifacts.AddCount);
    }

    [Fact]
    public async Task Handle_MismatchedEventType_RejectsArtifact()
    {
        var orderId = Guid.NewGuid();
        var source = new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Channel = "N8n",
            EventType = "OrderDecanted"
        };
        var handler = new RecordOrderArtifactCommandHandler(
            new ArtifactRepositoryStub(),
            new OutboxRepositoryStub(source));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new RecordOrderArtifactCommand(
                source.Id,
                orderId,
                OrderArtifactType.PostalReceipt,
                null,
                "telegram-file-id",
                "image/jpeg"),
            CancellationToken.None));
    }

    private sealed class ArtifactRepositoryStub : IOrderArtifactRepository
    {
        private OrderArtifact? _artifact;
        public int AddCount { get; private set; }
        public Task<OrderArtifact?> GetBySourceEventIdAsync(Guid sourceEventId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderArtifact?>(_artifact?.SourceEventId == sourceEventId ? _artifact : null);
        public Task AddAsync(OrderArtifact artifact, CancellationToken cancellationToken = default)
        {
            _artifact = artifact;
            AddCount++;
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OutboxRepositoryStub(NotificationOutbox source) : INotificationOutboxRepository
    {
        public Task AddAsync(NotificationOutbox notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(string channel, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<NotificationOutbox?>(source.Id == id ? source : null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
