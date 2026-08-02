using ZibasheERP.Application.Features.Orders.GetAdminOrders;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Orders;

public sealed class GetAdminOrdersQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsCustomerAndOperationalSummary()
    {
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Test", Mobile = "09120000000" };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            OrderNumber = "ZS-1001",
            Status = OrderStatus.Invoiced,
            FinalAmount = 900_000
        };
        order.Items.Add(new OrderItem { RequestedVolumeMl = 10 });
        var handler = new GetAdminOrdersQueryHandler(new OrderRepositoryStub(order));

        var result = await handler.Handle(
            new GetAdminOrdersQuery(OrderStatus.Invoiced),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal("Test", result.Single().CustomerName);
        Assert.Equal(10, result.Single().TotalVolumeMl);
    }

    private sealed class OrderRepositoryStub(Order order) : IOrderRepository
    {
        public Task<IReadOnlyCollection<Order>> GetForAdminAsync(OrderStatus? status, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(
                !status.HasValue || order.Status == status ? new[] { order } : Array.Empty<Order>());
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string number, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByExternalReferenceAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());
        public Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Order value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
