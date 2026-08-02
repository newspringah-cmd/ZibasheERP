using ZibasheERP.Application.Features.Payments.GetPaymentBalance;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Payments;

public sealed class GetPaymentBalanceQueryHandlerTests
{
    [Fact]
    public async Task Handle_SubtractsPendingAndConfirmedPayments()
    {
        var customer = new Customer { TelegramId = "123456789" };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ZS-1001",
            Customer = customer,
            FinalAmount = 1_000_000
        };
        order.Payments.Add(new Payment { Amount = 250_000, Status = PaymentStatus.Pending });
        order.Payments.Add(new Payment { Amount = 100_000, Status = PaymentStatus.Rejected });
        var handler = new GetPaymentBalanceQueryHandler(new OrderRepositoryStub(order));

        var result = await handler.Handle(
            new GetPaymentBalanceQuery(order.OrderNumber),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(750_000m, result!.RemainingAmount);
        Assert.Equal("123456789", result.TelegramId);
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public Task<Order?> GetByOrderNumberAsync(string number, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(order.OrderNumber == number ? order : null);
        public Task<Order?> GetByExternalReferenceAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(ZibasheERP.Domain.Entities.OrderStatus? status, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(new[] { order });
        public Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
