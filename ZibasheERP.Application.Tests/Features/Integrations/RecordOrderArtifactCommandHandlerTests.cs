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
            CustomerId = Guid.NewGuid(),
            OrderId = orderId,
            Channel = "N8n",
            EventType = "InvoiceIssued",
            Payload = "{\"Delivery\":{\"ChatId\":\"-1001234567890\"}}"
        };
        var artifacts = new ArtifactRepositoryStub();
        var outbox = new OutboxRepositoryStub(source);
        var handler = new RecordOrderArtifactCommandHandler(
            artifacts,
            outbox,
            new InvoiceRepositoryStub());
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
        Assert.Equal(0, outbox.Added.Count);
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
            new OutboxRepositoryStub(source),
            new InvoiceRepositoryStub());

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
        public Task<IReadOnlyCollection<OrderArtifact>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<OrderArtifact>>(
                _artifact?.OrderId == orderId ? new[] { _artifact } : Array.Empty<OrderArtifact>());
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
        public List<NotificationOutbox> Added { get; } = [];
        public Task AddAsync(NotificationOutbox notification, CancellationToken cancellationToken = default)
        {
            Added.Add(notification);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(string channel, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<NotificationOutbox?>(source.Id == id ? source : null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InvoiceRepositoryStub : IInvoiceRepository
    {
        public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Invoice?> GetForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Order?> GetOrderForInvoiceAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Invoice invoice, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
