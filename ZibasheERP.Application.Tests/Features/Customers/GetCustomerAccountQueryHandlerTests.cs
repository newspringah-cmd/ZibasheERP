using ZibasheERP.Application.Features.Customers.GetCustomerAccount;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Customers;

public sealed class GetCustomerAccountQueryHandlerTests
{
    [Fact]
    public async Task Handle_ByTelegramId_ReturnsSharedFinancialSummary()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Test Customer",
            Mobile = "09120000000",
            TelegramId = "123456789",
            WalletBalance = 500_000,
            CreditLimit = 2_000_000,
            CurrentDebt = 750_000,
            CanPlaceOrder = true
        };
        var handler = new GetCustomerAccountQueryHandler(new CustomerRepositoryStub(customer));

        var result = await handler.Handle(
            new GetCustomerAccountQuery(null, "123456789"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(customer.Id, result!.CustomerId);
        Assert.Equal(1_750_000m, result.AvailableCredit);
        Assert.Equal(750_000m, result.CurrentDebt);
    }

    [Fact]
    public async Task Handle_RejectsAmbiguousIdentifiers()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TelegramId = "123" };
        var handler = new GetCustomerAccountQueryHandler(new CustomerRepositoryStub(customer));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new GetCustomerAccountQuery(customer.Id, "123"),
                CancellationToken.None));
    }

    private sealed class CustomerRepositoryStub(Customer customer) : ICustomerRepository
    {
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Id == id ? customer : null);
        public Task<Customer?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.TelegramId == telegramId ? customer : null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
