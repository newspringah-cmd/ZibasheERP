using ZibasheERP.Application.Features.Invoices.GetOrderInvoice;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Invoices;

public sealed class GetOrderInvoiceQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsInvoiceForOrder()
    {
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Test", Mobile = "09120000000" };
        var order = new Order { Id = Guid.NewGuid(), CustomerId = customer.Id, Customer = customer };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Order = order,
            InvoiceNumber = "INV-TEST",
            IssuedAt = DateTime.UtcNow
        };
        var handler = new GetOrderInvoiceQueryHandler(new InvoiceRepositoryStub(invoice));

        var result = await handler.Handle(
            new GetOrderInvoiceQuery(order.Id),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("INV-TEST", result!.InvoiceNumber);
    }

    private sealed class InvoiceRepositoryStub(Invoice invoice) : IInvoiceRepository
    {
        public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Invoice?>(invoice.Id == id ? invoice : null);
        public Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Invoice?>(invoice.OrderId == orderId ? invoice : null);
        public Task<Invoice?> GetForUpdateByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Invoice?>(invoice.OrderId == orderId ? invoice : null);
        public Task<Order?> GetOrderForInvoiceAsync(Guid orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(invoice.OrderId == orderId ? invoice.Order : null);
        public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task AddAsync(Invoice value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
