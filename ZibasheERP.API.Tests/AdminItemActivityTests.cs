using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class AdminItemActivityTests
{
    public static void Run(Action<bool, string> check)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(
            "Server=example.invalid;Database=audit-tests;User Id=test;Password=unused;TrustServerCertificate=True").Options;
        var actor = new AdminActivityContext();
        void Begin(long update = 1, bool authorize = true)
        {
            actor.SetSource(123, "@admin_one", "First Admin", update);
            if (authorize) actor.Authorize(123);
        }
        void Capture(AppDbContext db) => typeof(AppDbContext).GetMethod("CaptureAdminItemActivities", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(db, null);
        AdminItemActivity[] Events(AppDbContext db) => db.ChangeTracker.Entries<AdminItemActivity>()
            .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToArray();
        SalesListRequest Request(SalesListRequestStatus status = SalesListRequestStatus.Confirmed)
        {
            var list = new SalesList { Id = Guid.NewGuid() };
            return new()
            {
                Id = Guid.NewGuid(), SalesListId = list.Id, SalesList = list, TelegramUserId = "customer", TelegramUsername = "customer",
                VolumeMl = 5, Status = status, CreatedByAdmin = true
            };
        }

        Begin();
        using (var db = new AppDbContext(options, actor))
        {
            var request = Request(); db.SalesListRequests.Add(request); Capture(db);
            var logged = Events(db).Single();
            check(logged.Action == AdminItemAction.Registered && logged.ActorTelegramUserId == 123 &&
                logged.ActorUsername == "admin_one" && logged.SalesListRequestId == request.Id && logged.NewVolumeMl == 5,
                "successful item registration carries the acting admin identity and request ID");
            Capture(db);
            check(Events(db).Length == 1 && Events(db).Single().Id == logged.Id,
                "repeated save preparation cannot duplicate the same audit event");
            check(db.Entry(request).State == EntityState.Added && db.Entry(Events(db).Single()).State == EntityState.Added,
                "item mutation and audit event are staged together for one database save");
        }
        Begin(2);
        using (var db = new AppDbContext(options, actor))
        {
            var request = Request(); db.Attach(request); request.VolumeMl = 8; Capture(db);
            var logged = Events(db).Single();
            check(logged.Action == AdminItemAction.VolumeChanged && logged.PreviousVolumeMl == 5 && logged.NewVolumeMl == 8,
                "volume change is one action with old and new amounts");
            request.VolumeMl = 9; Capture(db);
            check(Events(db).Length == 1 && Events(db).Single().NewVolumeMl == 9,
                "failed-save retry rebuilds the audit from current changes rather than retaining a stale amount");
        }
        Begin(3);
        using (var db = new AppDbContext(options, actor))
        {
            var request = Request(); db.Attach(request); request.Status = SalesListRequestStatus.Cancelled;
            request.IsDeleted = true; Capture(db);
            check(Events(db).Single().Action == AdminItemAction.Removed && Events(db).Single().PreviousVolumeMl == 5,
                "status cancellation and soft deletion count as one item removal");
        }
        Begin(4);
        using (var db = new AppDbContext(options, actor))
        {
            var request = Request(); db.Attach(request); db.Remove(request); Capture(db);
            check(Events(db).Single().Action == AdminItemAction.Removed,
                "physical deletion leaves an independent audit record");
            var model = db.Model.FindEntityType(typeof(AdminItemActivity))!;
            check(!model.GetForeignKeys().Any(), "audit history survives deletion of source items and lists");
        }
        Begin(5, false);
        using (var db = new AppDbContext(options, actor))
        {
            db.SalesListRequests.Add(Request()); Capture(db);
            actor.Authorize(999); Capture(db);
            check(Events(db).Length == 0, "customer actions or authorizing a different user cannot become admin activity");
        }
        Begin(6);
        using (var db = new AppDbContext(options, actor))
        {
            var pending = Request(SalesListRequestStatus.PendingConfirmation);
            db.Attach(pending); pending.VolumeMl = 9; Capture(db);
            check(Events(db).Length == 0, "unfinished draft editing is not counted as a successful item action");
            pending.Status = SalesListRequestStatus.Confirmed; Capture(db);
            check(Events(db).Single().Action == AdminItemAction.Registered,
                "confirming an admin draft counts registration once");
        }
        Begin(7);
        using (var db = new AppDbContext(options, actor))
        {
            var request = Request(); db.Attach(request); request.Status = SalesListRequestStatus.Invoiced; Capture(db);
            check(Events(db).Length == 0, "invoicing existing reservations does not inflate admin registration totals");
            var rollover = Request(); rollover.ExternalReference = "auto-next:old-list:old-request";
            db.Add(rollover); Capture(db);
            check(Events(db).Length == 0, "automatic queue carryover is excluded from admin registration totals");
        }
        Begin(8);
        using (var db = new AppDbContext(options, actor))
        {
            var inventory = Request(); inventory.SalesList.IsInventoryOffer = true;
            db.Add(inventory); Capture(db);
            check(Events(db).Length == 0, "inventory-offer requests are excluded from the sales-list-only activity report");
        }

        check(AdminItemActivityReport.CanView(123, 123, "private", "123") &&
            !AdminItemActivityReport.CanView(124, 124, "private", "123") &&
            !AdminItemActivityReport.CanView(123, -1001, "supergroup", "123") &&
            !AdminItemActivityReport.CanView(123, 123, "private", ""),
            "activity report is restricted to the primary owner's private chat, including forwarded buttons");
        check(new[] { 7, 15, 30 }.All(AdminItemActivityReport.IsSupportedPeriod) &&
            !AdminItemActivityReport.IsSupportedPeriod(0) && !AdminItemActivityReport.IsSupportedPeriod(31),
            "activity report accepts only weekly, fifteen-day and thirty-day windows");
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        AdminItemActivity Row(long admin, AdminItemAction action, DateTime at, string username = "admin_one") => new()
        { Id = Guid.NewGuid(), ActorTelegramUserId = admin, ActorUsername = username, Action = action, CreatedAt = at };
        var rows = new[]
        {
            Row(123, AdminItemAction.Registered, now.AddDays(-7)),
            Row(123, AdminItemAction.Removed, now.AddDays(-1), "renamed_admin"),
            Row(124, AdminItemAction.VolumeChanged, now.AddDays(-2), "admin_two"),
            Row(123, AdminItemAction.Registered, now.AddDays(-10)),
            Row(123, AdminItemAction.Registered, now.AddDays(-20)),
            Row(123, AdminItemAction.Registered, now.AddDays(-31)),
            Row(0, AdminItemAction.TrackingStarted, now.AddDays(-1)),
            Row(123, AdminItemAction.Registered, now)
        };
        var weekly = AdminItemActivityReport.Summarize(rows, now.AddDays(-7), now);
        check(weekly.Count == 2 && weekly.Single(row => row.UserId == 123).Registered == 1 &&
            weekly.Single(row => row.UserId == 123).Removed == 1 && weekly.Single(row => row.UserId == 124).VolumeChanged == 1,
            "weekly report separates each admin and action, includes start boundary and excludes end boundary");
        check(weekly.Single(row => row.UserId == 123).Username == "renamed_admin",
            "username changes do not split one admin into multiple identities");
        check(AdminItemActivityReport.Summarize(rows, now.AddDays(-15), now).Sum(row => row.Registered) == 2 &&
            AdminItemActivityReport.Summarize(rows, now.AddDays(-30), now).Sum(row => row.Registered) == 3,
            "fifteen-day and monthly reports use their own activity windows");
    }
}
