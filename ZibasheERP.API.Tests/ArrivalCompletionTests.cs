using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

public static class ArrivalCompletionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var list = new SalesList { Id = Guid.NewGuid(), PublicCode = 1234, StablePublicCode = 4559,
            PersianName = "عطر <مثال>", TotalVolume = 10, ReservedVolume = 10,
            ClosedDate = new DateTime(2026, 10, 16, 21, 15, 0, DateTimeKind.Unspecified) };
        string Caption(bool html) => (string)typeof(TelegramWebhookController)
            .GetMethod("FormatArrivalCardCaption", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list, html])!;
        var expected = "۱۴۰۵/۰۷/۲۵ — ساعت ۰۰:۴۵ (به وقت تهران)";
        check(SalesListCompletionDate.FormatDateTime(list, []) == expected && Caption(true).Contains(expected) && Caption(false).Contains(expected),
            "arrival photo and text cards show the saved completion date and time in Tehran across midnight");
        check(Caption(true).Contains("عطر &lt;مثال&gt;") && Caption(true).Contains("<code>4559</code>") &&
            !Caption(false).Contains("<b>"), "arrival completion details preserve HTML escaping and stable list codes");
        list.ClosedDate = null;
        check(Caption(true).Contains("تکمیل لیست: <b>ثبت نشده</b>"),
            "arrival report does not invent a completion time for historical lists");
        var valid = new SalesListRequest { Status = SalesListRequestStatus.Invoiced, Kind = SalesListRequestKind.CurrentBottle,
            ConfirmedAt = new DateTime(2026, 10, 16, 19, 41, 0, DateTimeKind.Utc) };
        list.Requests = [valid, new SalesListRequest { Status = SalesListRequestStatus.Cancelled, ConfirmedAt = DateTime.UtcNow.AddDays(1) }];
        check(Caption(false).Contains("۱۴۰۵/۰۷/۲۴ — ساعت ۲۳:۱۱ (به وقت تهران)"),
            "full historical arrival cards fall back to the last eligible confirmed item, ignoring cancelled requests");

        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var target = new SqlConnectionStringBuilder(connection);
        if (target.InitialCatalog != "ZibasheERPCloudDev" || target.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Arrival completion tests require the isolated development database");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        list.Requests = [];
        list.PublicCode = Random.Shared.Next(4000000, 5000000); list.StablePublicCode = null;
        list.PerfumeId = await db.Perfumes.Select(value => value.Id).FirstAsync();
        list.CreatedAt = DateTime.UtcNow;
        SalesListRequest Request(SalesListRequestStatus status, SalesListRequestKind kind, DateTime at, bool deleted = false) =>
            new() { Id = Guid.NewGuid(), SalesListId = list.Id, Status = status, Kind = kind,
                ConfirmedAt = at, CreatedAt = at, IsDeleted = deleted, TelegramUserId = "arrival-fixture",
                ExternalReference = "arrival-" + Guid.NewGuid().ToString("N"), VolumeMl = 1 };
        db.SalesLists.Add(list);
        db.SalesListRequests.AddRange(
            Request(SalesListRequestStatus.Invoiced, SalesListRequestKind.CurrentBottle, valid.ConfirmedAt!.Value),
            Request(SalesListRequestStatus.Confirmed, SalesListRequestKind.CurrentBottle, valid.ConfirmedAt.Value.AddMinutes(-1)),
            Request(SalesListRequestStatus.Cancelled, SalesListRequestKind.CurrentBottle, DateTime.UtcNow),
            Request(SalesListRequestStatus.Confirmed, SalesListRequestKind.NextBottle, DateTime.UtcNow),
            Request(SalesListRequestStatus.Confirmed, SalesListRequestKind.CurrentBottle, DateTime.UtcNow, true));
        await db.SaveChangesAsync();
        try
        {
            db.ChangeTracker.Clear();
            var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            typeof(TelegramWebhookController).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, db);
            var query = (IQueryable<SalesList>)typeof(TelegramWebhookController)
                .GetMethod("ArrivalListsWithCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null)!;
            var fetched = await query.SingleAsync(value => value.Id == list.Id);
            check(fetched.Requests.Count == 1 && fetched.Requests.Single().ConfirmedAt == valid.ConfirmedAt &&
                SalesListCompletionDate.FormatDateTime(fetched, fetched.Requests.ToArray()) == "۱۴۰۵/۰۷/۲۴ — ساعت ۲۳:۱۱ (به وقت تهران)",
                "arrival query loads only the latest eligible timestamp for fallback cards and subsequent selection refreshes");
        }
        finally
        {
            await db.SalesListRequests.Where(value => value.SalesListId == list.Id).ExecuteDeleteAsync();
            await db.SalesLists.Where(value => value.Id == list.Id).ExecuteDeleteAsync();
        }
    }
}
