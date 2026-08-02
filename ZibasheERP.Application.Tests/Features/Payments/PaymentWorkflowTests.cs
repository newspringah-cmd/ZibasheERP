using ZibasheERP.Application.Features.Payments.ConfirmPayment;
using ZibasheERP.Application.Features.Payments.SubmitPayment;
using ZibasheERP.Application.Features.Payments.RejectPayment;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Payments;

public sealed class PaymentWorkflowTests
{
    [Fact]
    public async Task SubmitPayment_CreatesPendingPayment()
    {
        var order = CreateOrder(1_000_000);
        var payments = new PaymentRepositoryStub();
        var handler = new SubmitPaymentCommandHandler(
            new OrderRepositoryStub(order),
            payments);

        var result = await handler.Handle(
            new SubmitPaymentCommand(
                order.Id,
                600_000,
                "CardToCard",
                "BANK-001",
                null),
            CancellationToken.None);

        Assert.NotNull(payments.AddedPayment);
        Assert.Equal(PaymentStatus.Pending, payments.AddedPayment!.Status);
        Assert.Equal(400_000m, result.RemainingAmount);
        Assert.True(payments.SaveChangesCalled);
    }

    [Fact]
    public async Task SubmitPayment_RejectsAmountAboveRemainingBalance()
    {
        var order = CreateOrder(1_000_000);
        order.Payments.Add(new Payment
        {
            Amount = 800_000,
            Status = PaymentStatus.Pending
        });
        var handler = new SubmitPaymentCommandHandler(
            new OrderRepositoryStub(order),
            new PaymentRepositoryStub());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new SubmitPaymentCommand(
                    order.Id,
                    300_000,
                    "CardToCard",
                    "BANK-002",
                    null),
                CancellationToken.None));

        Assert.Contains("بیشتر است", exception.Message);
    }

    [Fact]
    public async Task ConfirmPayment_WhenFullyPaid_UpdatesOrderAndCustomerDebt()
    {
        var order = CreateOrder(1_000_000);
        order.Customer!.CurrentDebt = 1_000_000;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Order = order,
            Amount = 1_000_000,
            Status = PaymentStatus.Pending
        };
        order.Payments.Add(payment);
        var payments = new PaymentRepositoryStub(payment);
        var outbox = new NotificationOutboxRepositoryStub();
        var handler = new ConfirmPaymentCommandHandler(payments, outbox);

        var result = await handler.Handle(
            new ConfirmPaymentCommand(payment.Id),
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Confirmed, payment.Status);
        Assert.True(payment.IsSuccessful);
        Assert.Equal(ZibasheERP.Domain.Entities.OrderStatus.Paid, order.Status);
        Assert.Equal(0m, order.Customer.CurrentDebt);
        Assert.Equal(0m, result.RemainingAmount);
        Assert.Equal("OrderPaid", outbox.AddedNotification?.EventType);
        Assert.True(payments.SaveChangesCalled);
    }

    [Fact]
    public async Task RejectPayment_MarksPaymentRejectedAndQueuesNotification()
    {
        var order = CreateOrder(1_000_000);
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Order = order,
            Amount = 1_000_000,
            Status = PaymentStatus.Pending
        };
        order.Payments.Add(payment);
        var payments = new PaymentRepositoryStub(payment);
        var outbox = new NotificationOutboxRepositoryStub();
        var handler = new RejectPaymentCommandHandler(payments, outbox);

        var result = await handler.Handle(
            new RejectPaymentCommand(payment.Id, "Bank reference not found"),
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Rejected, payment.Status);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("PaymentRejected", outbox.AddedNotification?.EventType);
        Assert.True(payments.SaveChangesCalled);
    }

    private static Order CreateOrder(decimal finalAmount)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Test Customer",
            Mobile = "09120000000",
            TelegramId = "123456789"
        };

        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            OrderNumber = "ZS-PAYMENT-TEST",
            FinalAmount = finalAmount,
            Status = ZibasheERP.Domain.Entities.OrderStatus.Registered
        };
    }

    private sealed class NotificationOutboxRepositoryStub : INotificationOutboxRepository
    {
        public NotificationOutbox? AddedNotification { get; private set; }
        public Task AddAsync(NotificationOutbox value, CancellationToken cancellationToken = default)
        {
            AddedNotification = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(Array.Empty<NotificationOutbox>());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<NotificationOutbox?>(null);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PaymentRepositoryStub(Payment? payment = null) : IPaymentRepository
    {
        public Payment? AddedPayment { get; private set; }
        public bool SaveChangesCalled { get; private set; }

        public Task AddAsync(Payment value, CancellationToken cancellationToken = default)
        {
            AddedPayment = value;
            return Task.CompletedTask;
        }

        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(payment?.Id == id ? payment : null);

        public Task<IReadOnlyCollection<Payment>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Payment>>(
                payment is null ? Array.Empty<Payment>() : new[] { payment });

        public Task<bool> TransactionIdExistsAsync(string transactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.Id == id ? order : null);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string externalReference, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(ZibasheERP.Domain.Entities.OrderStatus? status, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(new[] { order });
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
