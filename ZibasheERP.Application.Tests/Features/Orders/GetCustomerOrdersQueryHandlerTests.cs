using ZibasheERP.Application.Features.Orders.GetCustomerOrders;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Orders;

public sealed class GetCustomerOrdersQueryHandlerTests
{
    [Fact]
    public async Task Handle_WithTelegramId_ReturnsOrderSummaries()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TelegramId = "123456789"
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            OrderNumber = "ZS-TEST-1",
            FinalAmount = 900_000,
            RegisteredAt = DateTime.UtcNow,
            Items =
            {
                new OrderItem { RequestedVolumeMl = 2 },
                new OrderItem { RequestedVolumeMl = 3 }
            }
        };
        var handler = new GetCustomerOrdersQueryHandler(
            new CustomerRepositoryStub(customer),
            new OrderRepositoryStub(order));

        var result = await handler.Handle(
            new GetCustomerOrdersQuery(null, " 123456789 "),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        var summary = result.Single();
        Assert.Equal(order.Id, summary.Id);
        Assert.Equal(5, summary.TotalVolumeMl);
        Assert.Equal(2, summary.ItemCount);
    }

    [Fact]
    public async Task Handle_WithUnknownTelegramId_ReturnsEmptyList()
    {
        var handler = new GetCustomerOrdersQueryHandler(
            new CustomerRepositoryStub(null),
            new OrderRepositoryStub());

        var result = await handler.Handle(
            new GetCustomerOrdersQuery(null, "unknown"),
            CancellationToken.None);

        Assert.Equal(0, result.Count);
    }

    private sealed class CustomerRepositoryStub(Customer? customer) : ICustomerRepository
    {
        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer?.Id == id ? customer : null);
        public Task<Customer?> GetByTelegramIdAsync(string telegramId, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer?.TelegramId == telegramId ? customer : null);
        public Task<Customer?> GetByMobileAsync(string mobile, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer?.Mobile == mobile ? customer : null);
        public Task<Customer?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer?.Username == username ? customer : null);
        public Task AddAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Customer value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStub(params Order[] orders) : IOrderRepository
    {
        public Task<IReadOnlyCollection<Order>> GetByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(
                orders.Where(order => order.CustomerId == customerId).ToArray());
        public Task AddAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
