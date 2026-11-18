using System.Text.Json;
using ZibasheERP.Application.WebAccounts;
using ZibasheERP.Application.WebIdentity;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

// Hardcoded synthetic data only; no configuration, database, phone, addresses or endpoints are read.
if (args.Length != 1) throw new ArgumentException("Supply an output JSON path for the synthetic fixture.");
var customer = Guid.Parse("10000000-0000-0000-0000-000000000001");
var asOf = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
CustomerInvoiceReadSource Make(int n, decimal amount, InvoiceStatus status, decimal volume, decimal bottle, bool inventory) =>
    new(Guid.Parse($"20000000-0000-0000-0000-{n:D12}"), Guid.Parse($"30000000-0000-0000-0000-{n:D12}"), customer,
        $"DEMO-{n:D3}", asOf.AddDays(-4+n), status, OrderStatus.Invoiced, inventory, amount,
        n == 2 ? [new(Guid.Parse("40000000-0000-0000-0000-000000000002"), 50000, PaymentStatus.Confirmed)] : [],
        [new("عطر ساختگی قرارداد", volume, amount-bottle, bottle, inventory ? "باتل اصلی رایگان" : "شیشهٔ نمونه")]);
var rows = new[] {Make(1,380000,InvoiceStatus.Paid,2,20000,false),Make(2,150000,InvoiceStatus.Issued,1.5m,0,true),Make(3,530000,InvoiceStatus.Issued,5,30000,false)};
var actor = new CurrentWebActor(Guid.Parse("50000000-0000-0000-0000-000000000001"), customer,
    WebRole.Customer, WebAuthenticationStrength.CustomerTelegram, true);
var response = await new CustomerInvoiceReader(new FixtureStore(new(asOf,true,rows))).ReadMyInvoicesAsync(actor);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {WriteIndented=true};
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new {synthetic=true,response},options)+"\n");
Console.WriteLine("Synthetic customer invoice fixture exported.");

sealed class FixtureStore(CustomerInvoiceReadSnapshot snapshot) : ICustomerInvoiceReadStore
{
    public Task<CustomerInvoiceReadSnapshot> ReadAsync(Guid authenticatedCustomerId, CancellationToken ct) => Task.FromResult(snapshot);
}
