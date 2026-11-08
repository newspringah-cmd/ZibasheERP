using System.Globalization;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.Services;

public static class InvoicedSalesListReport
{
    public static bool TryParseDate(string text, bool persian, out DateTime date)
    {
        date = default;
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length != 3) return false;
        var numbers = new int[3];
        for (var index = 0; index < lines.Length; index++)
        {
            var normalized = string.Concat(lines[index].Trim().Select(c =>
                c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') :
                c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c));
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;
        }
        if (numbers[2] < (persian ? 1300 : 1900) || numbers[2] > (persian ? 1600 : 2200)) return false;
        try
        {
            date = persian
                ? new PersianCalendar().ToDateTime(numbers[2], numbers[1], numbers[0], 0, 0, 0, 0)
                : new DateTime(numbers[2], numbers[1], numbers[0]);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static (DateTime Since, DateTime Until) UtcRange(DateTime start, DateTime end)
    {
        if (end.Date < start.Date) throw new ArgumentException("End precedes start.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start.Date, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end.Date.AddDays(1), DateTimeKind.Unspecified), zone));
    }

    private static IQueryable<OrderItem> InvoicedItems(IQueryable<OrderItem> items, DateTime since, DateTime until) =>
        items.Where(item => !item.IsDeleted && item.SalesListId.HasValue &&
                item.Order != null && !item.Order.IsDeleted && item.Order.Status != OrderStatus.Cancelled &&
                item.Order.Source == OrderSource.SalesListInvoice &&
                item.Order.Invoices.Any(invoice => !invoice.IsDeleted &&
                    (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.Paid) &&
                    invoice.IssuedAt >= since && invoice.IssuedAt < until));

    private static IQueryable<Order> InvoicedOrders(IQueryable<Order> orders, DateTime since, DateTime until) =>
        orders.Where(order => !order.IsDeleted && order.SalesListId.HasValue &&
            order.Status != OrderStatus.Cancelled && order.Source == OrderSource.SalesListInvoice &&
            order.Invoices.Any(invoice => !invoice.IsDeleted &&
                (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.Paid) &&
                invoice.IssuedAt >= since && invoice.IssuedAt < until));

    // Count completed list cycles separately from fixed inventory packages.
    // Check both flags so older stock invoices cannot inflate the normal count.
    public static IQueryable<Guid> ListIds(IQueryable<OrderItem> items, DateTime since, DateTime until) =>
        InvoicedItems(items, since, until)
            .Where(item => !item.Order!.IsInventory && (item.SalesList == null || !item.SalesList.IsInventoryOffer))
            .Select(item => item.SalesListId!.Value).Distinct();

    public static IQueryable<Guid> ListIds(IQueryable<OrderItem> items, IQueryable<Order> orders, DateTime since, DateTime until) =>
        ListIds(items, since, until).Union(InvoicedOrders(orders, since, until)
            .Where(order => !order.IsInventory && (order.SalesList == null || !order.SalesList.IsInventoryOffer))
            .Select(order => order.SalesListId!.Value));

    public static IQueryable<Guid> InventoryIds(IQueryable<OrderItem> items, IQueryable<Order> orders, DateTime since, DateTime until) =>
        InvoicedItems(items, since, until)
            .Where(item => item.Order!.IsInventory || (item.SalesList != null && item.SalesList.IsInventoryOffer))
            .Select(item => item.SalesListId!.Value).Distinct()
            .Union(InvoicedOrders(orders, since, until)
                .Where(order => order.IsInventory || (order.SalesList != null && order.SalesList.IsInventoryOffer))
                .Select(order => order.SalesListId!.Value));
}
