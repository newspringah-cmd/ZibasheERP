using ZibasheERP.Application.WebAccounts;
using ZibasheERP.Application.WebIdentity;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

// The fake inbox lives in the local process output. Neither account maps to a real customer or receives SMS.
sealed class SyntheticAccounts : IVerifiedSmsCustomerDirectory, ICustomerLoginSmsSender, ICustomerInvoiceReadStore
{
    private static readonly SmsLoginCustomer[] Customers =
    [
        new(Guid.Parse("41000000-0000-0000-0000-000000000001"), "09000000001", true),
        new(Guid.Parse("41000000-0000-0000-0000-000000000002"), "09000000002", true)
    ];

    public Task<IReadOnlyList<SmsLoginCustomer>> FindByVerifiedMobileAsync(string mobile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SmsLoginCustomer>>(Customers.Where(customer => customer.VerifiedMobile == mobile).ToArray());

    public Task<SmsLoginCustomer?> GetAsync(Guid customerId, CancellationToken ct) =>
        Task.FromResult(Customers.SingleOrDefault(customer => customer.CustomerId == customerId));

    public Task SendLoginCodeAsync(string mobile, string code, CancellationToken ct)
    {
        Console.WriteLine($"PREVIEW_CODE {mobile} {code}");
        return Task.CompletedTask;
    }

    public Task<CustomerInvoiceReadSnapshot> ReadAsync(Guid customerId, CancellationToken ct)
    {
        var index = Array.FindIndex(Customers, customer => customer.CustomerId == customerId);
        if (index < 0) throw new InvalidOperationException("Unknown synthetic account.");
        var asOf = DateTimeOffset.Parse("2026-11-18T12:00:00+00:00");
        var number = index + 1;
        var amount = number * 150000m;
        var invoice = new CustomerInvoiceReadSource(Guid.Parse($"42000000-0000-0000-0000-{number:D12}"),
            Guid.Parse($"43000000-0000-0000-0000-{number:D12}"), customerId,
            $"PREVIEW-{number:D3}", asOf.AddDays(-1), InvoiceStatus.Issued, OrderStatus.Invoiced,
            true, amount, [], [new("عطر آزمایشی", 1.5m, amount, 0, "باتل اصلی رایگان")]);
        return Task.FromResult(new CustomerInvoiceReadSnapshot(asOf, true, [invoice]));
    }
}
