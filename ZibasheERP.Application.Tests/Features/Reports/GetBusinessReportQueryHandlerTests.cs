using ZibasheERP.Application.Features.Reports.GetBusinessReport;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Reports;

public sealed class GetBusinessReportQueryHandlerTests
{
    [Fact]
    public async Task Handle_CalculatesFinancialAndSalesMetrics()
    {
        var perfume = new Perfume { Id = Guid.NewGuid(), Name = "Test Perfume" };
        var activeOrder = new Order
        {
            Id = Guid.NewGuid(),
            RegisteredAt = DateTime.UtcNow.AddDays(-2),
            Status = OrderState.Paid,
            FinalAmount = 1_000_000
        };
        activeOrder.Items.Add(new OrderItem
        {
            PerfumeId = perfume.Id,
            Perfume = perfume,
            RequestedVolumeMl = 10,
            PerfumeAmount = 900_000
        });
        activeOrder.Payments.Add(new Payment
        {
            Amount = 1_000_000,
            Status = PaymentStatus.Confirmed
        });
        var cancelledOrder = new Order
        {
            Id = Guid.NewGuid(),
            RegisteredAt = DateTime.UtcNow.AddDays(-1),
            Status = OrderState.Cancelled,
            FinalAmount = 500_000
        };
        var debtor = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Test Debtor",
            Mobile = "09120000000",
            CurrentDebt = 400_000,
            CreditLimit = 2_000_000
        };
        var handler = new GetBusinessReportQueryHandler(
            new ReportingRepositoryStub(new[] { activeOrder, cancelledOrder }, new[] { debtor }, 400_000));

        var result = await handler.Handle(
            new GetBusinessReportQuery(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow),
            CancellationToken.None);

        Assert.Equal(2, result.TotalOrders);
        Assert.Equal(1, result.ActiveOrders);
        Assert.Equal(1, result.CancelledOrders);
        Assert.Equal(1_000_000m, result.GrossOrderAmount);
        Assert.Equal(1_000_000m, result.ConfirmedPaymentAmount);
        Assert.Equal(400_000m, result.OutstandingDebt);
        Assert.Equal(10, result.SoldVolumeMl);
        Assert.Equal("Test Perfume", result.TopPerfumes.Single().Name);
        Assert.Equal("Test Debtor", result.TopDebtors.Single().FullName);
    }

    [Fact]
    public async Task Handle_RejectsRangeLongerThanOneYear()
    {
        var handler = new GetBusinessReportQueryHandler(
            new ReportingRepositoryStub([], [], 0));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new GetBusinessReportQuery(DateTime.UtcNow.AddDays(-400), DateTime.UtcNow),
                CancellationToken.None));
    }

    private sealed class ReportingRepositoryStub(
        IReadOnlyCollection<Order> orders,
        IReadOnlyCollection<Customer> debtors,
        decimal debt) : IReportingRepository
    {
        public Task<IReadOnlyCollection<Order>> GetOrdersAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
            Task.FromResult(orders);
        public Task<IReadOnlyCollection<Customer>> GetTopDebtorsAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Customer>>(debtors.Take(limit).ToArray());
        public Task<decimal> GetTotalOutstandingDebtAsync(CancellationToken cancellationToken = default) => Task.FromResult(debt);
    }
}
