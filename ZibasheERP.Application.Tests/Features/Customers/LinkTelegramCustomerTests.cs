using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Customers;

public sealed class LinkTelegramCustomerTests
{
    [Fact]
    public async Task LinkByUsername_MatchesExistingCustomerAndStoresTelegramId()
    {
        var customer = new Customer { FullName = "Test", Username = "zibashe_user" };
        var repository = new CustomerRepositoryStub(customer);
        var handler = new LinkTelegramByUsernameCommandHandler(repository);

        var result = await handler.Handle(
            new LinkTelegramByUsernameCommand("123456789", "zibashe_user"),
            CancellationToken.None);

        Assert.Equal(LinkTelegramCustomerStatus.Linked, result.Status);
        Assert.Equal("123456789", customer.TelegramId);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task LinkByMobile_NormalizesTelegramInternationalNumber()
    {
        var customer = new Customer { FullName = "Test", Mobile = "09120000000" };
        var repository = new CustomerRepositoryStub(customer);
        var handler = new LinkTelegramCustomerCommandHandler(repository);

        var result = await handler.Handle(
            new LinkTelegramCustomerCommand("123456789", "+98 912 000 0000", "new_user"),
            CancellationToken.None);

        Assert.Equal(LinkTelegramCustomerStatus.Linked, result.Status);
        Assert.Equal("123456789", customer.TelegramId);
    }

    [Fact]
    public void MobileNormalizer_ConvertsPersianDigits()
    {
        Assert.Equal("09120000000", IranianMobileNormalizer.Normalize("۰۹۱۲۰۰۰۰۰۰۰"));
    }

    private sealed class CustomerRepositoryStub(Customer customer) : ICustomerRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<Customer?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.TelegramId == telegramId ? customer : null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Mobile == mobile ? customer : null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Username?.TrimStart('@') == username ? customer : null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
