using ZibasheERP.Application.Features.Shipments.GetShipmentTracking;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Shipments;

public sealed class GetShipmentTrackingQueryHandlerTests
{
    [Fact]
    public async Task Handle_ForOwner_ReturnsLatestShipmentTracking()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            OrderNumber = "ZS-TRACK-1",
            Status = OrderStatus.Shipped
        };
        order.Shipments.Add(new Shipment
        {
            Id = Guid.NewGuid(),
            ShippingCompany = "Post",
            TrackingCode = "TRACK-100",
            SentAt = DateTime.UtcNow
        });
        var handler = new GetShipmentTrackingQueryHandler(new OrderRepositoryStub(order));

        var result = await handler.Handle(
            new GetShipmentTrackingQuery(order.OrderNumber, null, customer.TelegramId),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("TRACK-100", result!.TrackingCode);
        Assert.Equal("Post", result.ShippingCompany);
        Assert.Equal("Shipped", result.OrderStatus);
    }

    [Fact]
    public async Task Handle_ForDifferentTelegramUser_ReturnsNull()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "owner" };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            OrderNumber = "ZS-TRACK-2"
        };
        var handler = new GetShipmentTrackingQueryHandler(new OrderRepositoryStub(order));

        var result = await handler.Handle(
            new GetShipmentTrackingQuery(order.OrderNumber, null, "other-user"),
            CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.OrderNumber == orderNumber ? order : null);
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>([]);
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>([]);
        public Task<bool> OrderNumberExistsAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
