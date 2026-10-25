using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class AddressReadyItemsTests
{
    public static void Run(Action<bool, string> check)
    {
        var customer = Guid.NewGuid();
        var other = Guid.NewGuid();
        var predicate = ShippingRecipientPolicy.ForCustomer(customer, "123", "@Receiver").Compile();
        OrderItem Item(Guid owner, bool gift = false, string? recipientId = null, string? recipientName = null) => new()
        {
            Order = new Order { CustomerId = owner },
            FulfillmentStatus = OrderItemFulfillmentStatus.DecantedReadyToShip,
            SourceSalesListRequest = new SalesListRequest { IsGift = gift,
                GiftRecipientTelegramUserId = recipientId, GiftRecipientTelegramUsername = recipientName }
        };
        check(predicate(Item(customer)) && !predicate(Item(other)), "address report includes own purchases only");
        check(!predicate(Item(customer, true, "999", "other")), "address report excludes gifts sent to someone else");
        check(predicate(Item(other, true, "123")) && predicate(Item(other, true, null, " @RECEIVER ")),
            "address report includes received gifts by stable ID or normalized username");
        check(!predicate(Item(other, true)) && !predicate(Item(customer, true)),
            "unknown gift recipients never fall back to the paying customer");
        var deleted = Item(customer); deleted.IsDeleted = true;
        var cancelled = Item(other, true, "123"); cancelled.Order!.Status = OrderStatus.Cancelled;
        var deletedOrder = Item(customer); deletedOrder.Order!.IsDeleted = true;
        check(!predicate(deleted) && !predicate(cancelled) && !predicate(deletedOrder),
            "deleted items and cancelled or deleted donor orders are excluded");
        check(!ShippingRecipientPolicy.ForCustomer(customer, null, null).Compile()(Item(other, true, "", "")),
            "missing customer identities cannot match empty recipient fields");
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=AddressReport;Trusted_Connection=True;TrustServerCertificate=True").Options);
        var sql = db.OrderItems.Where(ShippingRecipientPolicy.ForCustomer(customer, "123", "Receiver"))
            .Where(item => item.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip).ToQueryString();
        check(sql.Contains("GiftRecipientTelegramUsername") && sql.Contains("GiftRecipientTelegramUserId") && sql.Contains("FulfillmentStatus"),
            "gift ownership and ready status report filters translate to SQL");
        var fancy = Item(other, true, "123");
        fancy.Bottle = new Bottle { Type = BottleType.Fancy, Name = "فانتزی" };
        fancy.RequestedVolumeMl = 3;
        fancy.SalesList = new SalesList { PublicCode = 1234, PersianName = "عطر تست" };
        fancy.SourceSalesListRequest!.AdminNotes = "سرشیشه صورتی";
        fancy.SourceSalesListRequest.LabelIdentityText = "هدیه";
        string Format(params OrderItem[] items) => (string)typeof(TelegramWebhookController)
            .GetMethod("FormatAddressReadyItems", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [items])!;
        var text = Format(fancy);
        check(text.Contains("سرشیشه صورتی") && text.Contains("هدیه دریافتی") && text.Contains("1234") && text.Contains("عطر تست") && text.Contains("3 میل"),
            "address report displays fancy bottle instructions, received gift marker, perfume, volume and list code");
        check(text.Contains("لیبل: هدیه") && text.Contains("تعداد آیتم‌ها: 1"), "address report retains label instructions and item count");
        fancy.Bottle.Type = BottleType.Normal;
        check(!Format(fancy).Contains("سرشیشه صورتی"), "fancy bottle comment is not appended to normal bottle rows");
        check(Format().Contains("موردی ثبت نشده است.") && Format().Contains("تعداد آیتم‌ها: 0"),
            "address without ready items still contains an explicit empty report");
    }
}
