using ZibasheERP.Application.Features.Orders.CancelOrder;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;
using InvoiceState = ZibasheERP.Domain.Enums.InvoiceStatus;
using PaymentState = ZibasheERP.Domain.Enums.PaymentStatus;

namespace ZibasheERP.Application.Tests.Features.Orders;

public sealed class CancelOrderCommandHandlerTests
{
    [Fact]
    public async Task Handle_RollsBackDebtCapacityInvoiceAndPendingPayment()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TelegramId = "123456789",
            CurrentDebt = 1_000_000
        };
        var salesList = new SalesList
        {
            Id = Guid.NewGuid(),
            ReservedVolume = 10,
            TotalVolume = 10,
            Status = ZibasheERP.Domain.Entities.SalesListStatus.Full,
            HasBottleOwner = true,
            BottleOwnerCustomerId = customer.Id
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            SalesListId = salesList.Id,
            SalesList = salesList,
            OrderNumber = "ZS-CANCEL-TEST",
            FinalAmount = 1_000_000,
            Status = OrderStatus.Invoiced
        };
        order.Items.Add(new OrderItem { RequestedVolumeMl = 10, IsBottleOwner = true });
        var payment = new Payment { Status = PaymentState.Pending };
        order.Payments.Add(payment);
        var invoice = new Invoice { OrderId = order.Id, Status = InvoiceState.Issued };
        var orders = new OrderRepositoryStub(order);
        var outbox = new OutboxRepositoryStub();
        var handler = new CancelOrderCommandHandler(
            orders,
            new InvoiceRepositoryStub(invoice),
            outbox);

        var result = await handler.Handle(
            new CancelOrderCommand(order.Id, "Customer request"),
            CancellationToken.None);

        Assert.Equal("Cancelled", result.Status);
        Assert.Equal(0m, customer.CurrentDebt);
        Assert.Equal(0, salesList.ReservedVolume);
        Assert.Equal(ZibasheERP.Domain.Entities.SalesListStatus.Open, salesList.Status);
        Assert.False(salesList.HasBottleOwner);
        Assert.Equal(PaymentState.Cancelled, payment.Status);
        Assert.Equal(InvoiceState.Cancelled, invoice.Status);
        Assert.Equal("OrderCancelled", outbox.Added?.EventType);
        Assert.True(orders.SaveChangesCalled);
    }

    [Fact]
    public async Task Handle_RejectsOrderWithConfirmedPayment()
    {
        var order = CreateMinimalOrder();
        order.Payments.Add(new Payment { Status = PaymentState.Confirmed });
        var handler = new CancelOrderCommandHandler(
            new OrderRepositoryStub(order),
            new InvoiceRepositoryStub(null),
            new OutboxRepositoryStub());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new CancelOrderCommand(order.Id, "Customer request"),
                CancellationToken.None));
    }

    private static Order CreateMinimalOrder()
    {
        var customer = new Customer { Id = Guid.NewGuid() };
        var list = new SalesList { Id = Guid.NewGuid(), TotalVolume = 100 };
        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            SalesListId = list.Id,
            SalesList = list,
            FinalAmount = 100,
            Status = OrderStatus.Invoiced
        };
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) { SaveChangesCalled = true; return Task.CompletedTask; }
    }

    private sealed class InvoiceRepositoryStub(Invoice? invoice) : IInvoiceRepository
    {
        public Task<Invoice?> GetForUpdateByOrderIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(invoice);
        public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Invoice?> GetByOrderIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(null);
        public Task<Order?> GetOrderForInvoiceAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<bool> InvoiceNumberExistsAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Invoice value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OutboxRepositoryStub : INotificationOutboxRepository
    {
        public NotificationOutbox? Added { get; private set; }
        public Task AddAsync(NotificationOutbox value, CancellationToken cancellationToken = default) { Added = value; return Task.CompletedTask; }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(string channel, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<NotificationOutbox?>(null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
