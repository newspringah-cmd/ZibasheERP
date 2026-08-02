using ZibasheERP.Application.Features.Payments.GetPendingPayments;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Payments;

public sealed class GetPendingPaymentsQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsOrderAndCustomerDetails()
    {
        var customer = new Customer { FullName = "Test Customer", Mobile = "09120000000" };
        var order = new Order { Id = Guid.NewGuid(), OrderNumber = "ZS-1001", Customer = customer };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Order = order,
            Amount = 500_000,
            TransactionId = "TX-42"
        };
        var handler = new GetPendingPaymentsQueryHandler(new PaymentRepositoryStub(payment));

        var result = await handler.Handle(new GetPendingPaymentsQuery(), CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal("ZS-1001", result.Single().OrderNumber);
        Assert.Equal("Test Customer", result.Single().CustomerName);
    }

    private sealed class PaymentRepositoryStub(Payment payment) : IPaymentRepository
    {
        public Task<IReadOnlyCollection<Payment>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Payment>>(new[] { payment });
        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Payment?>(payment.Id == id ? payment : null);
        public Task<bool> TransactionIdExistsAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Payment value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
