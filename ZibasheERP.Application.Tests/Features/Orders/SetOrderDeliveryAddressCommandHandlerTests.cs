using ZibasheERP.Application.Features.Orders.SetDeliveryAddress;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Orders;

public sealed class SetOrderDeliveryAddressCommandHandlerTests
{
    [Fact]
    public async Task Handle_SetsAddressOwnedByOrderCustomer()
    {
        var customerId = Guid.NewGuid();
        var order = new Order { Id = Guid.NewGuid(), CustomerId = customerId, Status = OrderStatus.Paid };
        var address = new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            City = "Tehran",
            FullAddress = "Test address"
        };
        var repository = new OrderRepositoryStub(order);
        var handler = new SetOrderDeliveryAddressCommandHandler(
            repository,
            new AddressRepositoryStub(address));

        var result = await handler.Handle(
            new SetOrderDeliveryAddressCommand(order.Id, address.Id),
            CancellationToken.None);

        Assert.Equal(address.Id, order.DeliveryAddressId);
        Assert.Equal("Tehran", result.City);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task Handle_RejectsAddressOwnedByAnotherCustomer()
    {
        var order = new Order { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid() };
        var address = new Address { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid() };
        var handler = new SetOrderDeliveryAddressCommandHandler(
            new OrderRepositoryStub(order),
            new AddressRepositoryStub(address));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new SetOrderDeliveryAddressCommand(order.Id, address.Id),
                CancellationToken.None));
    }

    private sealed class AddressRepositoryStub(Address address) : IAddressRepository
    {
        public Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Address?>(address.Id == id ? address : null);
        public Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Address>>(Array.Empty<Address>());
        public Task AddAsync(Address value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
