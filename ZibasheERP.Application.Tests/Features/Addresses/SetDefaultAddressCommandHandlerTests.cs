using ZibasheERP.Application.Features.Addresses.SetDefaultAddress;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Addresses;

public sealed class SetDefaultAddressCommandHandlerTests
{
    [Fact]
    public async Task Handle_ByTelegramId_ChangesDefaultWithinOwnedAddresses()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var oldDefault = new Address { Id = Guid.NewGuid(), CustomerId = customer.Id, IsDefault = true };
        var selected = new Address { Id = Guid.NewGuid(), CustomerId = customer.Id };
        var repository = new AddressRepositoryStub(oldDefault, selected);
        var handler = new SetDefaultAddressCommandHandler(
            new CustomerRepositoryStub(customer),
            repository);

        await handler.Handle(
            new SetDefaultAddressCommand(selected.Id, null, customer.TelegramId),
            CancellationToken.None);

        Assert.False(oldDefault.IsDefault);
        Assert.True(selected.IsDefault);
        Assert.True(repository.Saved);
    }

    [Fact]
    public async Task Handle_RejectsAddressOwnedByAnotherCustomer()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var otherAddress = new Address { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid() };
        var handler = new SetDefaultAddressCommandHandler(
            new CustomerRepositoryStub(customer),
            new AddressRepositoryStub(otherAddress));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new SetDefaultAddressCommand(otherAddress.Id, null, customer.TelegramId),
            CancellationToken.None));
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
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Id == id ? customer : null);
        public Task<Customer?> GetByTelegramIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.TelegramId == id ? customer : null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
