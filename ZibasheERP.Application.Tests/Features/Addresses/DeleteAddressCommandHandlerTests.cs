using ZibasheERP.Application.Features.Addresses.DeleteAddress;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Addresses;

public sealed class DeleteAddressCommandHandlerTests
{
    [Fact]
    public async Task Handle_DeletesDefaultAndSelectsReplacement()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var selected = new Address { Id = Guid.NewGuid(), CustomerId = customer.Id, IsDefault = true };
        var replacement = new Address { Id = Guid.NewGuid(), CustomerId = customer.Id };
        var addresses = new AddressRepositoryStub(selected, replacement);
        var handler = new DeleteAddressCommandHandler(
            new CustomerRepositoryStub(customer),
            addresses,
            new OrderRepositoryStub());

        await handler.Handle(
            new DeleteAddressCommand(selected.Id, null, customer.TelegramId),
            CancellationToken.None);

        Assert.True(selected.IsDeleted);
        Assert.False(selected.IsDefault);
        Assert.True(replacement.IsDefault);
        Assert.True(addresses.Saved);
    }

    [Fact]
    public async Task Handle_RejectsAddressUsedByActiveOrder()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var address = new Address { Id = Guid.NewGuid(), CustomerId = customer.Id, IsDefault = true };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            DeliveryAddressId = address.Id,
            Status = OrderStatus.ReadyToShip
        };
        var handler = new DeleteAddressCommandHandler(
            new CustomerRepositoryStub(customer),
            new AddressRepositoryStub(address),
            new OrderRepositoryStub(order));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new DeleteAddressCommand(address.Id, null, customer.TelegramId),
            CancellationToken.None));

        Assert.False(address.IsDeleted);
    }

    private sealed class AddressRepositoryStub(params Address[] addresses) : IAddressRepository
    {
        public bool Saved { get; private set; }
        public Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Address?>(addresses.FirstOrDefault(address => address.Id == id));
        public Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Address>>(addresses.Where(address => address.CustomerId == id).ToArray());
        public Task AddAsync(Address value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saved = true;
            return Task.CompletedTask;
        }
    }

    private sealed class CustomerRepositoryStub(Customer customer) : ICustomerRepository
    {
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(customer.Id == id ? customer : null);
        public Task<Customer?> GetByTelegramIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(customer.TelegramId == id ? customer : null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStub(params Order[] orders) : IOrderRepository
    {
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(orders.Where(order => order.CustomerId == customerId).ToArray());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task AddAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string externalReference, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
