using ZibasheERP.Application.Features.Integrations.GetOrderArtifacts;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Integrations;

public sealed class GetOrderArtifactsQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsArtifactsOnlyForTelegramOwner()
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Customer = new Customer { TelegramId = "123456789" }
        };
        var artifact = new OrderArtifact
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Type = OrderArtifactType.DecantPhoto,
            ExternalFileId = "telegram-file-id"
        };
        var handler = new GetOrderArtifactsQueryHandler(
            new OrderRepositoryStub(order),
            new ArtifactRepositoryStub(artifact));

        var owned = await handler.Handle(
            new GetOrderArtifactsQuery(order.Id, "123456789"),
            CancellationToken.None);
        var unauthorized = await handler.Handle(
            new GetOrderArtifactsQuery(order.Id, "987654321"),
            CancellationToken.None);

        Assert.Equal(1, owned.Count);
        Assert.Equal(0, unauthorized.Count);
    }

    private sealed class ArtifactRepositoryStub(OrderArtifact artifact) : IOrderArtifactRepository
    {
        public Task<OrderArtifact?> GetBySourceEventIdAsync(Guid sourceEventId, CancellationToken cancellationToken = default) => Task.FromResult<OrderArtifact?>(null);
        public Task<IReadOnlyCollection<OrderArtifact>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<OrderArtifact>>(artifact.OrderId == orderId ? new[] { artifact } : Array.Empty<OrderArtifact>());
        public Task AddAsync(OrderArtifact value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string externalReference, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
