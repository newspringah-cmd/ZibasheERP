using ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Addresses;

public sealed class GetCustomerAddressesQueryHandlerTests
{
    [Fact]
    public async Task Handle_ResolvesTelegramCustomerAndReturnsAddresses()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123456789" };
        var address = new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ReceiverName = "Test",
            Mobile = "09120000000",
            City = "Tehran",
            FullAddress = "Test address",
            IsDefault = true
        };
        var handler = new GetCustomerAddressesQueryHandler(
            new CustomerRepositoryStub(customer),
            new AddressRepositoryStub(address));

        var result = await handler.Handle(
            new GetCustomerAddressesQuery(null, customer.TelegramId),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal(address.Id, result.Single().Id);
        Assert.True(result.Single().IsDefault);
    }

    private sealed class AddressRepositoryStub(Address address) : IAddressRepository
    {
        public Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Address>>(address.CustomerId == id ? new[] { address } : Array.Empty<Address>());
        public Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Address?>(address.Id == id ? address : null);
        public Task AddAsync(Address value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
