using ZibasheERP.Application.Features.Customers.ManageCustomers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Customers;

public sealed class CustomerManagementTests
{
    [Fact]
    public async Task SearchCustomers_ReturnsFinancialIdentitySummary()
    {
        var customer = CreateCustomer();
        var handler = new SearchCustomersQueryHandler(new AdminCustomerRepositoryStub(customer));

        var result = await handler.Handle(
            new SearchCustomersQuery("zibashe_user", true),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal("zibashe_user", result.Single().Username);
        Assert.Equal(1_500_000m, result.Single().AvailableCredit);
    }

    [Fact]
    public async Task SetCustomerAccess_BlockingAlsoDisablesNewOrders()
    {
        var customer = CreateCustomer();
        var repository = new AdminCustomerRepositoryStub(customer);
        var handler = new SetCustomerAccessCommandHandler(repository);

        var result = await handler.Handle(
            new SetCustomerAccessCommand(customer.Id, true, true),
            CancellationToken.None);

        Assert.True(customer.IsBlocked);
        Assert.False(customer.CanPlaceOrder);
        Assert.False(result.CanPlaceOrder);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task SetCustomerCredit_UpdatesLimitAndWalletWithoutChangingDebt()
    {
        var customer = CreateCustomer();
        var repository = new AdminCustomerRepositoryStub(customer);
        var handler = new SetCustomerCreditCommandHandler(repository);

        var result = await handler.Handle(
            new SetCustomerCreditCommand(customer.Id, 5_000_000, 1_000_000),
            CancellationToken.None);

        Assert.Equal(5_000_000m, customer.CreditLimit);
        Assert.Equal(1_000_000m, customer.WalletBalance);
        Assert.Equal(500_000m, customer.CurrentDebt);
        Assert.Equal(5_500_000m, result.AvailableCredit);
    }

    private static Customer CreateCustomer() => new()
    {
        Id = Guid.NewGuid(),
        FullName = "Test Customer",
        Mobile = "09120000000",
        TelegramId = "123456789",
        Username = "zibashe_user",
        CreditLimit = 2_000_000,
        CurrentDebt = 500_000,
        CanPlaceOrder = true
    };

    private sealed class AdminCustomerRepositoryStub(params Customer[] customers) : IAdminCustomerRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(customers.FirstOrDefault(customer => customer.Id == id));
        public Task<IReadOnlyCollection<Customer>> SearchAsync(string? search, bool debtOnly, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Customer>>(customers
                .Where(customer => !debtOnly || customer.CurrentDebt > 0)
                .Where(customer => string.IsNullOrWhiteSpace(search) ||
                    customer.FullName.Contains(search) ||
                    customer.Mobile.Contains(search) ||
                    customer.Username == search ||
                    customer.TelegramId == search)
                .Take(limit).ToArray());
        public Task UpdateAsync(Customer customer, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
