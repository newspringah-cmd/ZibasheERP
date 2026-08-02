using ZibasheERP.Application.Features.Orders.AdvanceFulfillment;
using ZibasheERP.Application.Features.Shipments.CreateShipment;
using ZibasheERP.Application.Features.Shipments.MarkDelivered;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Shipments;

public sealed class FulfillmentAndShipmentTests
{
    [Fact]
    public async Task AdvanceFulfillment_AllowsPaidToDecanted()
    {
        var order = new Order { Id = Guid.NewGuid(), Status = OrderStatus.Paid };
        var repository = new OrderRepositoryStub(order);
        var handler = new AdvanceFulfillmentCommandHandler(repository);

        var result = await handler.Handle(
            new AdvanceFulfillmentCommand(order.Id, OrderStatus.Decanted),
            CancellationToken.None);

        Assert.Equal(OrderStatus.Decanted, order.Status);
        Assert.Equal("Paid", result.PreviousStatus);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task CreateShipment_SnapshotsAddressAndMarksOrderShipped()
    {
        var customerId = Guid.NewGuid();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Customer = new Customer { Id = customerId, TelegramId = "123456789" },
            OrderNumber = "ZS-SHIP-TEST",
            Status = OrderStatus.ReadyToShip
        };
        var address = new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ReceiverName = "Test Receiver",
            Mobile = "09120000000",
            Province = "Tehran",
            City = "Tehran",
            PostalCode = "1234567890",
            FullAddress = "Test address"
        };
        var repository = new ShipmentRepositoryStub(order, address);
        var outbox = new NotificationOutboxRepositoryStub();
        var handler = new CreateShipmentCommandHandler(repository, outbox);

        var result = await handler.Handle(
            new CreateShipmentCommand(
                order.Id,
                address.Id,
                "Post",
                150_000,
                "TRACK-001",
                null),
            CancellationToken.None);

        Assert.NotNull(repository.AddedShipment);
        Assert.Equal(address.FullAddress, repository.AddedShipment!.FullAddress);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal("TRACK-001", result.TrackingCode);
        Assert.Equal("OrderShipped", outbox.AddedNotification?.EventType);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task MarkDelivered_UpdatesShipmentAndOrder()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            OrderNumber = "ZS-DELIVERY-TEST",
            Status = OrderStatus.Shipped
        };
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Order = order,
            SentAt = DateTime.UtcNow.AddDays(-1)
        };
        var repository = new ShipmentRepositoryStub(order, new Address(), shipment);
        var outbox = new NotificationOutboxRepositoryStub();
        var handler = new MarkShipmentDeliveredCommandHandler(repository, outbox);

        var result = await handler.Handle(
            new MarkShipmentDeliveredCommand(shipment.Id),
            CancellationToken.None);

        Assert.True(shipment.IsDelivered);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal("Delivered", result.OrderStatus);
        Assert.Equal("OrderDelivered", outbox.AddedNotification?.EventType);
        Assert.True(repository.SaveChangesCalled);
    }

    private sealed class NotificationOutboxRepositoryStub : INotificationOutboxRepository
    {
        public NotificationOutbox? AddedNotification { get; private set; }
        public Task AddAsync(NotificationOutbox value, CancellationToken cancellationToken = default)
        {
            AddedNotification = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<NotificationOutbox?>(null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) { SaveChangesCalled = true; return Task.CompletedTask; }
    }

    private sealed class ShipmentRepositoryStub(
        Order order,
        Address address,
        Shipment? existingShipment = null) : IShipmentRepository
    {
        public Shipment? AddedShipment { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public Task<Order?> GetOrderForShippingAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Shipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(existingShipment?.Id == id ? existingShipment : null);
        public Task<Address?> GetAddressAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Address?>(address.Id == id ? address : null);
        public Task<bool> TrackingCodeExistsAsync(string trackingCode, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Shipment shipment, CancellationToken cancellationToken = default) { AddedShipment = shipment; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) { SaveChangesCalled = true; return Task.CompletedTask; }
    }
}
