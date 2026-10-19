using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class CustomerListReservationTests
{
    public static void Run(Action<bool,string> check)
    {
        var list = Guid.NewGuid();
        var existing = new SalesListRequest { Id = Guid.NewGuid(), SalesListId = list, TelegramUserId = "123",
            TelegramUsername = "Customer", VolumeMl = 5, Status = SalesListRequestStatus.Confirmed };
        bool Matches(string id = "123", string? name = "customer", Guid? cycle = null) =>
            CustomerListReservationPolicy.ExistingItem(cycle ?? list, id, name).Compile()(existing);
        check(Matches(), "existing own item blocks a second item regardless of selected volume");
        check(Matches("other-id", "@CUSTOMER") && Matches("123", "changed_username"),
            "duplicate identity matches normalized username or stable Telegram ID");
        existing.CreatedByAdmin = true;
        check(Matches(), "customer cannot duplicate an item previously registered by an admin");
        check(!CustomerListReservationPolicy.RequiresSingleItem(existing),
            "authorized admin registrations remain exempt from the single-item restriction");
        existing.CreatedByAdmin = false;
        check(CustomerListReservationPolicy.RequiresSingleItem(existing),
            "ordinary customer registration must enforce the single-item restriction");
        existing.IsBottleOwner = true;
        check(Matches(), "bottle owner's own item also prevents customer duplicate registration");
        check(!Matches(cycle: Guid.NewGuid()) && !Matches("999", "other"),
            "new list cycle and different customers remain independent");
        existing.Status = SalesListRequestStatus.Cancelled;
        check(!Matches(), "cancelled own item does not block a new reservation");
        existing.Status = SalesListRequestStatus.PendingConfirmation;
        check(!Matches(), "pending request can resume without counting as a confirmed duplicate");
        existing.Status = SalesListRequestStatus.Invoiced;
        check(Matches(), "invoiced reservation still prevents duplication within its own cycle");
        existing.IsDeleted = true;
        check(!Matches(), "deleted reservation does not block a new item");
        existing.IsDeleted = false; existing.IsGift = true;
        check(!Matches(), "gift registration is separate from the customer's own item");
        existing.IsGift = false; existing.Kind = SalesListRequestKind.NextBottle;
        check(!Matches(), "next-bottle queue does not block a current-bottle reservation");
        existing.Kind = SalesListRequestKind.CurrentBottle;
        check(!CustomerListReservationPolicy.ExistingItem(list,"123","customer",existing.Id).Compile()(existing),
            "confirming the same request remains idempotent");
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(
            "Server=example.invalid;Database=test;User Id=test;Password=unused;TrustServerCertificate=True").Options);
        var sql = db.SalesListRequests.Where(CustomerListReservationPolicy.ExistingItem(list,"123","@customer")).ToQueryString();
        check(sql.Contains("REPLACE") && sql.Contains("SalesListId"), "duplicate predicate translates to a server-side SQL query");
    }
}
