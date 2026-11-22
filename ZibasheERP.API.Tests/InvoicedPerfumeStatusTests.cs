using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class InvoicedPerfumeStatusTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var sql = new SqlConnectionStringBuilder(connection);
        if (sql.InitialCatalog != "ZibasheERPCloudDev" || sql.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Isolated test database required");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var stamp = Guid.NewGuid().ToString("N")[..12];
        var now = DateTime.UtcNow;
        var perfume = new Perfume { Id = Guid.NewGuid(), Name = "رایحه تست " + stamp, EnglishName = "Test Scent " + stamp };
        var donor = new Customer { Id = Guid.NewGuid(), FullName = "Donor", Username = "donor_" + stamp, Mobile = "091" + stamp };
        var receiver = new Customer { Id = Guid.NewGuid(), FullName = "Receiver", Username = "receiver_" + stamp, Mobile = "092" + stamp };
        var stableCode = Random.Shared.Next(41000000, 42000000);
        SalesList List(int offset) => new()
        {
            Id = Guid.NewGuid(), PerfumeId = perfume.Id, PublicCode = stableCode + offset, StablePublicCode = stableCode,
            PersianName = perfume.Name, EnglishName = perfume.EnglishName, OpenDate = now.AddDays(offset - 20), TotalVolume = 100
        };
        var old = List(1);
        var recent = List(2);
        var stock = List(3);
        stock.IsInventoryOffer = true;
        stock.StablePublicCode = null;
        var giftRequest = new SalesListRequest
        {
            Id = Guid.NewGuid(), SalesListId = recent.Id, TelegramUserId = "admin-username:" + donor.Username,
            TelegramUsername = donor.Username, IsGift = true, GiftRecipientTelegramUsername = receiver.Username,
            Status = SalesListRequestStatus.Invoiced, VolumeMl = 3, ExternalReference = "invoiced-status:" + stamp
        };
        var orders = new List<Order>();
        OrderItem Add(SalesList cycle, OrderItemFulfillmentStatus status, decimal volume, int count = 1,
            InvoiceStatus invoiceStatus = InvoiceStatus.Paid, bool legacy = false, bool owner = false, bool gift = false)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(), CustomerId = donor.Id, SalesListId = cycle.Id,
                OrderNumber = Guid.NewGuid().ToString("N"), Status = OrderStatus.Invoiced
            };
            order.Invoices.Add(new Invoice { Id = Guid.NewGuid(), OrderId = order.Id, InvoiceNumber = Guid.NewGuid().ToString("N"), Status = invoiceStatus });
            var item = new OrderItem
            {
                Id = Guid.NewGuid(), OrderId = order.Id, SalesListId = legacy ? null : cycle.Id, Quantity = count,
                RequestedVolumeMl = volume, FulfillmentStatus = status, IsBottleOwner = owner,
                SourceSalesListRequestId = gift ? giftRequest.Id : null
            };
            order.Items.Add(item);
            orders.Add(order);
            return item;
        }
        Add(old, OrderItemFulfillmentStatus.Shipped, 1, legacy: true);
        Add(recent, OrderItemFulfillmentStatus.DecantedReadyToShip, 3, count: 2, gift: true);
        Add(recent, OrderItemFulfillmentStatus.DecantedReadyToShip, 1, count: 5, invoiceStatus: InvoiceStatus.Issued);
        Add(recent, OrderItemFulfillmentStatus.Shipped, 40, owner: true);
        Add(recent, OrderItemFulfillmentStatus.DecantQueue, 11, invoiceStatus: InvoiceStatus.Cancelled);
        Add(stock, OrderItemFulfillmentStatus.DecantQueue, 19);
        var ids = orders.Select(order => order.Id).ToArray();
        var listIds = new[] { old.Id, recent.Id, stock.Id };
        db.AddRange(perfume, donor, receiver, old, recent, stock, giftRequest);
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync();

        try
        {
            const BindingFlags methods = BindingFlags.NonPublic | BindingFlags.Instance;
            var sender = DispatchProxy.Create<ITelegramMessageSender, InvoicedPerfumeReportSender>();
            var capture = (InvoicedPerfumeReportSender)(object)sender;
            var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach (var (field, value) in new (string, object)[] { ("_db", db), ("_sender", sender),
                         ("_logger", NullLogger<TelegramWebhookController>.Instance) })
                typeof(TelegramWebhookController).GetField(field, methods)!.SetValue(controller, value);

            var search = typeof(TelegramWebhookController).GetMethod("InvoicedPerfumeSearch", methods)!;
            IQueryable<Perfume> Matches(string input) => (IQueryable<Perfume>)search.Invoke(controller, [input])!;
            var persianCode = string.Concat(stableCode.ToString().Select(c => (char)('۰' + c - '0')));
            check(await Matches(persianCode).Select(p => p.Id).SingleAsync() == perfume.Id &&
                await Matches("رايحه‌تست " + stamp).Select(p => p.Id).SingleAsync() == perfume.Id &&
                await Matches("TEST SCENT " + stamp).Select(p => p.Id).SingleAsync() == perfume.Id,
                "invoiced perfume status supports Persian digits, Persian spelling variants and English names");
            check(!await Matches(stock.PublicCode.ToString()).AnyAsync(),
                "a stock-only code does not appear in the completed sales-list report");
            await (Task)typeof(TelegramWebhookController).GetMethod("SendInvoicedPerfumeStatusAsync", methods)!
                .Invoke(controller, [741471928L, perfume.Id, CancellationToken.None])!;
            var report = string.Join("\n", capture.Reports);
            check(report.Contains($"لیست {stableCode} — نوبت 1 از 2") && report.Contains($"لیست {stableCode} — نوبت 2 از 2") &&
                report.Contains($"@{receiver.Username} 🎁 — 3 میل × 2") && report.Contains("1 میل — ارسال‌شده") &&
                !report.Contains("11 میل") && !report.Contains("19 میل"),
                "report renders distinct cycles with the real gift recipient, legacy items, and no cancelled or inventory lines");
            check(capture.Summary.Contains("7 دکانت (2 × 3 میل، 5 × 1 میل)") &&
                capture.Summary.Contains("ارسال‌شده — 1 دکانت (1 × 1 میل)") &&
                capture.Summary.Contains("ارسال‌شده — 1 باتل اصلی (1 × 40 میل)") && capture.Summary.Contains("تعداد کل اقلام: 9"),
                "summary totals physical quantities by volume and fulfillment status with original bottles counted separately");
        }
        finally
        {
            await db.OrderItems.Where(i => ids.Contains(i.OrderId)).ExecuteDeleteAsync();
            await db.Invoices.Where(i => ids.Contains(i.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r => r.Id == giftRequest.Id).ExecuteDeleteAsync();
            await db.SalesLists.Where(l => listIds.Contains(l.Id)).ExecuteDeleteAsync();
            await db.Perfumes.Where(p => p.Id == perfume.Id).ExecuteDeleteAsync();
            await db.Customers.Where(c => c.Id == donor.Id || c.Id == receiver.Id).ExecuteDeleteAsync();
        }
    }
}

public class InvoicedPerfumeReportSender : DispatchProxy
{
    public List<string> Reports { get; } = [];
    public string Summary { get; private set; } = string.Empty;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var message = (string)args![1]!;
        if (method!.Name == "SendAsync") Reports.Add(message);
        else if (method.Name == "SendInlineKeyboardAsync") Summary = message;
        else throw new NotSupportedException(method.Name);
        return Task.FromResult(new TelegramSendResult(true));
    }
}
