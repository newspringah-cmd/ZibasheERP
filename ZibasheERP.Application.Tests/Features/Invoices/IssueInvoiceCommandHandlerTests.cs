using ZibasheERP.Application.Features.Invoices.IssueInvoice;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Invoices;

public sealed class IssueInvoiceCommandHandlerTests
{
    [Fact]
    public async Task Handle_CopiesOrderSnapshotAndMarksOrderInvoiced()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Invoice Customer",
            Mobile = "09120000000",
            TelegramId = "123456789"
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Customer = customer,
            CustomerId = customer.Id,
            OrderNumber = "ZS-INVOICE-TEST",
            PerfumeTotal = 900_000,
            BottleTotal = 100_000,
            FinalAmount = 1_000_000,
            Items = { new OrderItem { Id = Guid.NewGuid(), RequestedVolumeMl = 2 } }
        };
        var repository = new InvoiceRepositoryStub(order);
        var outbox = new NotificationOutboxRepositoryStub();
        var handler = new IssueInvoiceCommandHandler(repository, outbox);

        var result = await handler.Handle(
            new IssueInvoiceCommand(order.Id),
            CancellationToken.None);

        Assert.NotNull(repository.AddedInvoice);
        Assert.Equal(InvoiceStatus.Issued, repository.AddedInvoice!.Status);
        Assert.Equal(1_000_000m, result.TotalAmount);
        Assert.Equal(ZibasheERP.Domain.Entities.OrderStatus.Invoiced, order.Status);
        Assert.Equal("InvoiceIssued", outbox.AddedNotification?.EventType);
        Assert.True(repository.SaveChangesCalled);
    }

    private sealed class InvoiceRepositoryStub(Order order) : IInvoiceRepository
    {
        public Invoice? AddedInvoice { get; private set; }
        public bool SaveChangesCalled { get; private set; }

        public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Invoice?> GetForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Order?> GetOrderForInvoiceAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.Id == orderId ? order : null);
        public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task AddAsync(Invoice invoice, CancellationToken cancellationToken = default)
        {
            AddedInvoice = invoice;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class NotificationOutboxRepositoryStub : INotificationOutboxRepository
    {
        public NotificationOutbox? AddedNotification { get; private set; }
        public Task AddAsync(NotificationOutbox value, CancellationToken cancellationToken = default)
        {
            AddedNotification = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(string channel, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<NotificationOutbox?>(null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
