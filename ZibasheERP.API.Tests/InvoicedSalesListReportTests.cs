using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class InvoicedSalesListReportTests
{
    public static void Run(Action<bool, string> check)
    {
        check(InvoicedSalesListReport.TryParseDate("۲\n۷\n۱۴۰۵", true, out var persian) && persian == new DateTime(2026, 9, 24),
            "report accepts three-line Persian date with Persian digits");
        check(InvoicedSalesListReport.TryParseDate("٢٤\r\n٩\r\n٢٠٢٦", false, out var gregorian) && gregorian == persian,
            "report accepts Gregorian dates and Arabic digits in day-month-year order");
        check(!InvoicedSalesListReport.TryParseDate("1405/07/02", true, out _) &&
            !InvoicedSalesListReport.TryParseDate("31\n7\n1405", true, out _) &&
            !InvoicedSalesListReport.TryParseDate("29\n2\n2026", false, out _) &&
            !InvoicedSalesListReport.TryParseDate("1\n1\n1405", false, out _),
            "report rejects malformed dates, invalid days and dates from the wrong selected calendar");
        check(InvoicedSalesListReport.TryParseDate("30\n12\n1399", true, out _) &&
            !InvoicedSalesListReport.TryParseDate("30\n12\n1400", true, out _),
            "report validates Persian leap years");
        var (since, until) = InvoicedSalesListReport.UtcRange(persian, persian);
        check(since == new DateTime(2026, 9, 23, 20, 30, 0, DateTimeKind.Utc) && until == since.AddDays(1),
            "same-day report includes the entire Tehran day using exclusive UTC end");
        var reversedRejected = false;
        try { InvoicedSalesListReport.UtcRange(persian, persian.AddDays(-1)); }
        catch (ArgumentException) { reversedRejected = true; }
        check(reversedRejected, "report rejects end date before start date");

        var list = Guid.NewGuid();
        OrderItem Item(Guid? id, DateTime issued, InvoiceStatus status = InvoiceStatus.Issued,
            bool deletedInvoice = false, bool deletedOrder = false, bool deletedItem = false, bool cancelledOrder = false) => new()
        {
            SalesListId = id, IsDeleted = deletedItem,
            Order = new Order { IsDeleted = deletedOrder, Status = cancelledOrder ? OrderStatus.Cancelled : OrderStatus.Invoiced,
                Invoices = new List<Invoice> { new() { IssuedAt = issued, Status = status, IsDeleted = deletedInvoice } } }
        };
        var rows = new[] {
            Item(list, since), Item(list, since.AddHours(1), InvoiceStatus.Paid),
            Item(Guid.NewGuid(), until.AddTicks(-1)), Item(Guid.NewGuid(), until),
            Item(Guid.NewGuid(), since.AddTicks(-1)), Item(null, since),
            Item(Guid.NewGuid(), since, InvoiceStatus.Draft), Item(Guid.NewGuid(), since, InvoiceStatus.Cancelled),
            Item(Guid.NewGuid(), since, deletedInvoice: true), Item(Guid.NewGuid(), since, deletedOrder: true),
            Item(Guid.NewGuid(), since, deletedItem: true), Item(Guid.NewGuid(), since, cancelledOrder: true)
        };
        check(InvoicedSalesListReport.ListIds(rows.AsQueryable(), since, until).Count() == 2,
            "report counts distinct cycles and includes paid invoices, excluding invalid records and range boundaries");
        rows[0].Order!.Invoices.Add(new Invoice { IssuedAt = since, Status = InvoiceStatus.Paid });
        check(InvoicedSalesListReport.ListIds(rows.AsQueryable(), since, until).Count() == 2,
            "multiple customer invoices or reissued invoices cannot multiply a list count");
        var legacy = Item(Guid.NewGuid(), since).Order!;
        legacy.SalesListId = Guid.NewGuid();
        var duplicate = Item(list, since).Order!;
        duplicate.SalesListId = list;
        check(InvoicedSalesListReport.ListIds(rows.AsQueryable(), new[] { legacy, duplicate }.AsQueryable(), since, until).Count() == 3,
            "legacy order-level list references are included without double-counting modern references");
        legacy.Source = OrderSource.ManualInvoice;
        check(InvoicedSalesListReport.ListIds(rows.AsQueryable(), new[] { legacy }.AsQueryable(), since, until).Count() == 2,
            "manual invoices do not inflate sales list counts");
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=ReportQuery;Trusted_Connection=True;TrustServerCertificate=True").Options);
        var sql = InvoicedSalesListReport.ListIds(db.OrderItems, db.Orders, since, until).ToQueryString();
        check(sql.Contains("EXISTS") && sql.Contains("IssuedAt") && sql.Contains("UNION") && !sql.Contains("UNION ALL"),
            "list-count report translates to server-side SQL with distinct cycles and invoice-date filtering");
        check(AdminItemActivityReport.CanView(123, 123, "private", "123") &&
            !AdminItemActivityReport.CanView(456, 456, "private", "123") &&
            !AdminItemActivityReport.CanView(123, -100, "supergroup", "123"),
            "report access is limited to the primary owner's private chat");
    }
}
