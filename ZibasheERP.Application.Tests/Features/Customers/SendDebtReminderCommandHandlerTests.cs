using ZibasheERP.Application.Features.Customers.SendDebtReminder;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Customers;

public sealed class SendDebtReminderCommandHandlerTests
{
    [Fact]
    public async Task Handle_QueuesReliableTelegramReminder()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Test Customer",
            Mobile = "09120000000",
            TelegramId = "123456789",
            CurrentDebt = 750_000
        };
        var outbox = new OutboxRepositoryStub();
        var handler = new SendDebtReminderCommandHandler(
            new AdminCustomerRepositoryStub(customer),
            outbox);

        var result = await handler.Handle(
            new SendDebtReminderCommand(customer.Id, "Please settle"),
            CancellationToken.None);

        Assert.Equal("DebtReminder", outbox.Added?.EventType);
        Assert.Equal("123456789", outbox.Added?.Recipient);
        Assert.NotNull(outbox.Added);
        Assert.Contains("750000", outbox.Added!.Payload);
        Assert.Equal("Pending", result.Status);
        Assert.True(outbox.SaveChangesCalled);
    }

    [Fact]
    public async Task Handle_RejectsCustomerWithoutDebt()
    {
        var customer = new Customer { Id = Guid.NewGuid(), CurrentDebt = 0, TelegramId = "123" };
        var handler = new SendDebtReminderCommandHandler(
            new AdminCustomerRepositoryStub(customer),
            new OutboxRepositoryStub());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new SendDebtReminderCommand(customer.Id, null), CancellationToken.None));
    }

    private sealed class AdminCustomerRepositoryStub(Customer customer) : IAdminCustomerRepository
    {
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Customer?>(customer.Id == id ? customer : null);
        public Task<IReadOnlyCollection<Customer>> SearchAsync(string? search, bool debtOnly, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Customer>>(new[] { customer });
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OutboxRepositoryStub : INotificationOutboxRepository
    {
        public NotificationOutbox? Added { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public Task AddAsync(NotificationOutbox notification, CancellationToken cancellationToken = default)
        {
            Added = notification;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(string channel, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>([]);
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<NotificationOutbox?>(null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
