using ZibasheERP.Application.Features.Addresses.AddTelegramAddress;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Addresses;

public sealed class AddTelegramAddressCommandHandlerTests
{
    [Fact]
    public async Task Handle_NormalizesPostalCodeAndMakesFirstAddressDefault()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TelegramId = "123456789",
            Mobile = "09120000000"
        };
        var addresses = new AddressRepositoryStub();
        var handler = new AddTelegramAddressCommandHandler(
            new CustomerRepositoryStub(customer),
            addresses);

        var result = await handler.Handle(
            new AddTelegramAddressCommand(
                customer.TelegramId,
                "منزل",
                "Test Receiver",
                "Tehran",
                "Tehran",
                "۱۲۳۴۵۶۷۸۹۰",
                "Test address"),
            CancellationToken.None);

        Assert.Equal("1234567890", result.PostalCode);
        Assert.True(result.IsDefault);
        Assert.NotNull(addresses.Added);
    }

    private sealed class AddressRepositoryStub : IAddressRepository
    {
        public Address? Added { get; private set; }
        public Task<Address?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Address?>(null);
        public Task<IReadOnlyCollection<Address>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Address>>(Array.Empty<Address>());
        public Task AddAsync(Address value, CancellationToken cancellationToken = default)
        {
            Added = value;
            return Task.CompletedTask;
        }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CustomerRepositoryStub(Customer customer) : ICustomerRepository
    {
        public Task<Customer?> GetByTelegramIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.TelegramId == id ? customer : null);
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
