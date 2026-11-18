using System.Text.Json;
using Xunit;
using ZibasheERP.Application.WebAccounts;
using ZibasheERP.Application.WebIdentity;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.Tests.Features.WebAccounts;

public sealed class CustomerInvoiceReaderTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
    private static CurrentWebActor Actor => new(Guid.NewGuid(), Customer, WebRole.Customer, WebAuthenticationStrength.CustomerTelegram, true);
    private sealed class Store(params CustomerInvoiceReadSource[] invoices) : ICustomerInvoiceReadStore
    {
        public bool Complete { get; set; } = true;
        public int Calls { get; private set; }
        public Guid RequestedCustomer { get; private set; }
        public Task<CustomerInvoiceReadSnapshot> ReadAsync(Guid id, CancellationToken ct)
        {
            Calls++; RequestedCustomer = id;
            return Task.FromResult(new CustomerInvoiceReadSnapshot(AsOf, Complete, invoices));
        }
    }
    private static CustomerInvoiceReadSource Invoice(decimal amount = 100000, InvoiceStatus status = InvoiceStatus.Issued) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Customer, "DEMO-001", AsOf.AddDays(-1), status, OrderStatus.Invoiced,
            false, amount, [], [new("عطر نمونه", 1, amount, 0, "نرمال ۱ میل")]);
    private static InvoicePaymentReadSource Payment(decimal amount, PaymentStatus status = PaymentStatus.Confirmed, bool deleted = false) =>
        new(Guid.NewGuid(), amount, status, deleted);

    [Fact]
    public async Task Read_RejectsMissingInactiveAndStaffActorBeforeLoadingData()
    {
        var store = new Store(Invoice()); var reader = new CustomerInvoiceReader(store);
        foreach (var actor in new CurrentWebActor?[] { null, Actor with { IsActive = false }, Actor with { Roles = WebRole.Accountant }, Actor with { CustomerId = null } })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadMyInvoicesAsync(actor));
        Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task Read_DerivesCustomerFromTrustedActor_AndRejectsWrongOwnerRows()
    {
        var store = new Store(Invoice()); await new CustomerInvoiceReader(store).ReadMyInvoicesAsync(Actor);
        Assert.Equal(Customer, store.RequestedCustomer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(Invoice() with { CurrentCustomerId = Guid.NewGuid() })).ReadMyInvoicesAsync(Actor));
    }
    [Fact]
    public async Task Read_ExcludesDraftCancelledDeletedInvoicesAndCancelledOrders()
    {
        var store = new Store(Invoice(), Invoice(status: InvoiceStatus.Draft), Invoice(status: InvoiceStatus.Cancelled),
            Invoice() with { IsDeleted = true }, Invoice() with { IsOrderDeleted = true }, Invoice() with { OrderStatus = OrderStatus.Cancelled });
        var result = await new CustomerInvoiceReader(store).ReadMyInvoicesAsync(Actor);
        Assert.Equal(1, result.TotalCount); Assert.Equal("100000", result.Summary.Outstanding);
    }
    [Fact]
    public async Task Read_OnlyConfirmedNondeletedPaymentsReduceDebt()
    {
        var source = Invoice() with { Payments = [Payment(25000), Payment(90000, PaymentStatus.Pending), Payment(90000, PaymentStatus.Rejected),
            Payment(90000, PaymentStatus.Refunded), Payment(90000, PaymentStatus.Cancelled), Payment(90000, deleted: true)] };
        var result = await new CustomerInvoiceReader(new Store(source)).ReadMyInvoicesAsync(Actor);
        Assert.Equal("25000", result.Summary.ConfirmedPartialPayments); Assert.Equal("75000", result.Summary.Outstanding);
    }
    [Fact]
    public async Task Read_PaidLegacyStatusDoesNotRequireInventingPaymentRows()
    {
        var result = await new CustomerInvoiceReader(new Store(Invoice(status: InvoiceStatus.Paid))).ReadMyInvoicesAsync(Actor);
        Assert.Equal("100000", result.Summary.PaidInvoiceTotal); Assert.Equal("0", result.Summary.Outstanding);
        Assert.Equal("0", result.Invoices.Single().ConfirmedPayments); Assert.Equal("paid", result.Invoices.Single().Status);
    }
    [Fact]
    public async Task Read_OverpaymentNeverCreatesNegativeDebtOrWalletCredit()
    {
        var result = await new CustomerInvoiceReader(new Store(Invoice() with { Payments = [Payment(150000)] })).ReadMyInvoicesAsync(Actor);
        Assert.Equal("150000", result.Summary.ConfirmedPartialPayments); Assert.Equal("0", result.Summary.Outstanding);
    }
    [Fact]
    public async Task Read_DuplicateActiveInvoiceOrReusedPaymentStopsReport()
    {
        var first = Invoice();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(first, first with { InvoiceId = Guid.NewGuid() })).ReadMyInvoicesAsync(Actor));
        var payment = Payment(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(first with { Payments = [payment] }, Invoice() with { Payments = [payment] })).ReadMyInvoicesAsync(Actor));
    }
    [Fact]
    public async Task Read_IncompleteSnapshotMismatchedTotalsAndNegativeConfirmedPaymentStopReport()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(Invoice()) { Complete = false }).ReadMyInvoicesAsync(Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(Invoice() with { TotalAmount = 5 })).ReadMyInvoicesAsync(Actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomerInvoiceReader(new Store(Invoice() with { Payments = [Payment(-1)] })).ReadMyInvoicesAsync(Actor));
    }
    [Fact]
    public async Task Read_PaginationHasStableOrderingAndSummaryAcrossAllPages()
    {
        var oldest = Invoice() with { IssuedAt = AsOf.AddDays(-2) }; var latest = Invoice(200000);
        var reader = new CustomerInvoiceReader(new Store(oldest, latest));
        var one = await reader.ReadMyInvoicesAsync(Actor, 1, 1); var two = await reader.ReadMyInvoicesAsync(Actor, 2, 1);
        Assert.Equal(latest.InvoiceId, one.Invoices.Single().Id); Assert.Equal(oldest.InvoiceId, two.Invoices.Single().Id);
        Assert.Equal(one.Summary, two.Summary); Assert.Equal("300000", one.Summary.Outstanding);
        Assert.True(one.HasMore); Assert.False(two.HasMore);
        Assert.Equal(0, (await reader.ReadMyInvoicesAsync(Actor, 3, 1)).Invoices.Count);
    }
    [Fact]
    public async Task Read_RejectsInvalidPageBeforeCallingStore()
    {
        var store = new Store(); var reader = new CustomerInvoiceReader(store);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadMyInvoicesAsync(Actor, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadMyInvoicesAsync(Actor, pageSize: 51));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadMyInvoicesAsync(Actor, int.MaxValue, 50));
        Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task Read_PreservesHistoricalAmountsHalfMlAndDecimalPrecision()
    {
        const decimal amount = 9007199254740993.25m;
        var source = Invoice(amount) with { IsInventory = true, Lines = [new("موجودی نمونه", 1.5m, amount, 0, "باتل اصلی رایگان")] };
        var result = await new CustomerInvoiceReader(new Store(source)).ReadMyInvoicesAsync(Actor);
        Assert.Equal("9007199254740993.25", result.Invoices.Single().Total); Assert.Equal("1.5", result.Invoices.Single().Lines.Single().VolumeMl);
        Assert.Equal("inventory", result.Invoices.Single().Source); Assert.Equal("IRT", result.Currency);
    }
    [Fact]
    public async Task Read_MatchesCurrentFinanceFormulaOnSyntheticFixture()
    {
        var all = new[] { Invoice(380000, InvoiceStatus.Paid), Invoice(150000) with { Payments = [Payment(50000)] }, Invoice(530000) };
        var result = await new CustomerInvoiceReader(new Store(all)).ReadMyInvoicesAsync(Actor);
        var pending = all.Where(i => i.Status == InvoiceStatus.Issued).ToArray();
        // Independently reproduce the existing CustomerInvoiceFinanceService report on known fixtures.
        var legacyOutstanding = pending.Sum(i => Math.Max(0, i.TotalAmount - i.Payments.Where(p => !p.IsDeleted && p.Status == PaymentStatus.Confirmed).Sum(p => p.Amount)));
        Assert.Equal("380000", result.Summary.PaidInvoiceTotal); Assert.Equal("680000", result.Summary.PendingInvoiceTotal);
        Assert.Equal("50000", result.Summary.ConfirmedPartialPayments);
        Assert.Equal(legacyOutstanding.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Summary.Outstanding);
    }
    [Fact]
    public async Task Read_WhitelistedOutputHasNoCustomerIdentityOrInternalLocationFields()
    {
        var result = await new CustomerInvoiceReader(new Store(Invoice())).ReadMyInvoicesAsync(Actor);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(json.Contains("customerId", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("telegram", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("location", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("purchaseCost", StringComparison.OrdinalIgnoreCase));
    }
}
