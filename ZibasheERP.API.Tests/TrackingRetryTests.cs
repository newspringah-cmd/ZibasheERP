using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;

public static class TrackingRetryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTime.UtcNow;
        check(TrackingDispatchRetryPolicy.NextAttempt(now, 1, 120) >= now.AddSeconds(120),
            "tracking retry honors the complete Telegram cooldown instead of ten seconds");
        check(TrackingDispatchRetryPolicy.NextAttempt(now, 100) <= now.AddMinutes(5),
            "interrupted tracking work has bounded backoff");
        var delivery = new TrackingDispatchDelivery { AttemptedAt = now };
        var dispatch = new TrackingDispatch { Status = TrackingDispatchStatus.Sending, Deliveries = [delivery] };
        check(!TrackingDispatchRetryPolicy.CanResume(dispatch), "unknown photo delivery is never automatically resent");
        delivery.SentAt = now;
        check(TrackingDispatchRetryPolicy.CanResume(dispatch), "known accepted photo can resume bookkeeping after a crash");
        delivery.AttemptedAt = delivery.SentAt = null;
        check(TrackingDispatchRetryPolicy.CanResume(dispatch), "claimed dispatch without an HTTP attempt can resume after restart");
        dispatch.Status = TrackingDispatchStatus.NeedsReview;
        check(!TrackingDispatchRetryPolicy.CanResume(dispatch), "unmatched recipients never enter automatic retries");
        dispatch.Status = TrackingDispatchStatus.Failed;
        dispatch.LastError = "گروه فعال مشتری پیدا نشد";
        check(!TrackingDispatchRetryPolicy.CanResume(dispatch), "missing recipient group requires review rather than endless sends");

        using var sender = new TelegramMessageSender(Options.Create(new TelegramOptions { BotToken = "test-token" }));
        var field = typeof(TelegramMessageSender).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(sender)!).Dispose();
        var handler = new TrackingHttpHandler();
        field.SetValue(sender, new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
        var rejected = await sender.SendPhotoBytesWithKeyboardAsync("test-chat", [1], "test.png", "test", []);
        check(rejected.IsRetryable && rejected.RetryAfterSeconds == 120 && handler.Calls == 1,
            "multipart 429 returns a definite rejection and exact retry_after for durable scheduling");
        handler.Code = HttpStatusCode.ServiceUnavailable;
        var ambiguous = await sender.SendPhotoBytesWithKeyboardAsync("test-chat", [1], "test.png", "test", []);
        check(!ambiguous.IsRetryable, "ambiguous server failure does not authorize duplicate photo sends");

        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connection)) await RunSql(connection, check);
    }

    private static async Task RunSql(string connection, Action<bool, string> check)
    {
        var selector = new SqlConnectionStringBuilder(connection);
        if (selector.InitialCatalog != "ZibasheERPCloudDev" || selector.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Tracking integration tests require the isolated local development database");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        await using var db = new AppDbContext(options);
        var customer = new Customer { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, FullName = "Tracking integration fixture", Mobile = Guid.NewGuid().ToString("N")[..18] };
        var batch = new TrackingImportBatch { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, SourceHash = Guid.NewGuid().ToString("N"), Carrier = TrackingCarrier.IranPost };
        var group = new CustomerTelegramGroup { Id = Guid.NewGuid(), CustomerId = customer.Id, ChatId = "tracking-fixture-only", Title = "Fake transport only", LinkedAt = DateTime.UtcNow };
        var dispatches = Enumerable.Range(0, 40).Select(_ => new TrackingDispatch {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, ImportBatchId = batch.Id,
            CustomerId = customer.Id, Carrier = TrackingCarrier.IranPost, TrackingCode = Guid.NewGuid().ToString("N"),
            Status = TrackingDispatchStatus.Ready, CardImage = [1], RecipientName = "Fixture" }).ToArray();
        var ids = dispatches.Select(value => value.Id).ToArray();
        var guardOrders = new List<Guid>();
        db.Add(customer); db.Add(group); db.Add(batch); db.AddRange(dispatches);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var sender = DispatchProxy.Create<ITelegramMessageSender, TrackingSenderProxy>();
        var transport = (TrackingSenderProxy)(object)sender;
        TelegramWebhookController Controller(AppDbContext context)
        {
            var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach (var (name, value) in new (string, object)[] {
                ("_db", context), ("_sender", sender), ("_options", new TelegramOptions()),
                ("_logger", NullLogger<TelegramWebhookController>.Instance) })
                typeof(TelegramWebhookController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, value);
            return controller;
        }
        async Task Queue(TelegramWebhookController controller) => await (Task)typeof(TelegramWebhookController)
            .GetMethod("ConfirmTrackingBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, new object?[] { 1L, batch.Id, CancellationToken.None, null })!;
        async Task Process(TelegramWebhookController controller, Guid id) => await (Task)typeof(TelegramWebhookController)
            .GetMethod("ProcessTrackingBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(controller, new object?[] { 1L, batch.Id, CancellationToken.None, id, true })!;
        try
        {
            var controller = Controller(db);
            await Queue(controller); await Queue(controller);
            check(await db.NotificationOutbox.CountAsync(value => ids.Contains(value.Id)) == 40 && transport.PhotoCalls == 0,
                "confirming forty dispatches twice commits forty durable jobs and sends no photos on the webhook");
            for (var i = 0; i < 24; i++) await Process(controller, ids[i]);
            try { await Process(controller, ids[24]); throw new Exception("Expected rate limit"); }
            catch (TrackingDispatchRetryException error) { check(error.Seconds == 120, "twenty-fifth delivery requests the full server wait"); }
            check(await db.TrackingDispatches.CountAsync(value => ids.Contains(value.Id) && value.Status == TrackingDispatchStatus.Sent) == 24,
                "forty-item simulation preserves the first twenty-four successes during a rate limit");
            var rateDelivery = await db.TrackingDispatchDeliveries.AsNoTracking().SingleAsync(value => value.TrackingDispatchId == ids[24]);
            check(rateDelivery.AttemptedAt is null && rateDelivery.SentAt is null,
                "definite 429 rejection releases only that unsent destination for safe retry");
            await using var restarted = new AppDbContext(options);
            var restartedController = Controller(restarted);
            foreach (var id in ids) await Process(restartedController, id);
            check(await restarted.TrackingDispatches.CountAsync(value => ids.Contains(value.Id) && value.Status == TrackingDispatchStatus.Sent) == 40 &&
                transport.PhotoCalls == 41 && transport.Accepted == 40,
                "after context restart all forty complete with no resend of the first twenty-four");
            var repository = new NotificationOutboxRepository(restarted);
            var lease = (await repository.GetPendingAsync("TrackingDispatch", 1)).Single(value => ids.Contains(value.Id));
            await using var other = new AppDbContext(options);
            var next = (await new NotificationOutboxRepository(other).GetPendingAsync("TrackingDispatch", 1)).Single();
            check(next.Id != lease.Id, "parallel worker cannot claim an already leased tracking job");
            await other.NotificationOutbox.Where(value => value.Id == lease.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LockedUntil, DateTime.UtcNow.AddMinutes(-1)));
            var recovered = await new NotificationOutboxRepository(other).GetPendingAsync("TrackingDispatch", 100);
            check(recovered.Any(value => value.Id == lease.Id), "expired tracking job lease survives process loss and becomes claimable again");
            var requestId=Guid.NewGuid();
            // A valid anchor and a manual gift with no confirmed recipient must
            // stop the transport before an HTTP attempt is journalled.
            for(var n=0;n<2;n++)
            {
                var order=new Order{Id=Guid.NewGuid(),CustomerId=customer.Id,OrderNumber="GUARD-"+Guid.NewGuid().ToString("N"),
                    Source=n==0?OrderSource.SalesListInvoice:OrderSource.ManualInvoice,Notes=n==0?null:"فاکتور دستی | هدیه برای نامشخص",CreatedAt=DateTime.UtcNow,Status=OrderStatus.Invoiced};
                guardOrders.Add(order.Id);db.Orders.Add(order);
                db.OrderItems.Add(new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,ShippingRequestId=requestId,ShippingRequestedAt=DateTime.UtcNow,
                    RequestedVolumeMl=2,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,CreatedAt=DateTime.UtcNow});
            }
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            await db.TrackingDispatchDeliveries.Where(d=>d.TrackingDispatchId==ids[0]).ExecuteDeleteAsync();
            await db.TrackingDispatches.Where(d=>d.Id==ids[0]).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.Status,TrackingDispatchStatus.Ready)
                .SetProperty(d=>d.ShippingRequestId,(Guid?)requestId).SetProperty(d=>d.SentAt,(DateTime?)null));
            var attempts=transport.PhotoCalls;
            await Process(Controller(db),ids[0]);db.ChangeTracker.Clear();
            check(transport.PhotoCalls==attempts&&!await db.TrackingDispatchDeliveries.AnyAsync(d=>d.TrackingDispatchId==ids[0]),
                "ambiguous parcel is rejected before any tracking-photo HTTP call or delivery attempt record");
            check(await db.TrackingDispatches.AnyAsync(d=>d.Id==ids[0]&&d.Status==TrackingDispatchStatus.NeedsReview)&&
                !await db.OrderItems.AnyAsync(i=>guardOrders.Contains(i.OrderId)&&i.ShippedAt!=null),
                "recipient validation failure enters needs-review and preserves every item shipping status");
            await Process(Controller(db),ids[0]);
            check(transport.PhotoCalls==attempts,"needs-review recipient failure is never retried as an automatic Telegram send");
            await db.Orders.Where(o=>o.Id==guardOrders[1]).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Notes,(string?)null));
            await db.TrackingDispatches.Where(d=>d.Id==ids[0]).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.Status,TrackingDispatchStatus.Ready));
            transport.AfterAcceptance=async()=>
            {
                await using var concurrent=new AppDbContext(options);
                await concurrent.Orders.Where(o=>o.Id==guardOrders[1]).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Notes,"فاکتور دستی | هدیه برای نامشخص"));
            };
            await Process(Controller(db),ids[0]);await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check(await db.TrackingDispatchDeliveries.AnyAsync(d=>d.TrackingDispatchId==ids[0]&&d.SentAt!=null)&&
                await db.TrackingDispatches.AnyAsync(d=>d.Id==ids[0]&&d.Status==TrackingDispatchStatus.NeedsReview)&&
                !await db.OrderItems.AnyAsync(i=>guardOrders.Contains(i.OrderId)&&i.ShippedAt!=null),
                "recipient mutation during accepted photo delivery rolls back shipping and stays needs-review even after a later SaveChanges");
            await Process(Controller(db),ids[0]);
            check(transport.PhotoCalls==attempts+1,"known accepted tracking photo is not resent after late recipient validation stops bookkeeping");
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.NotificationOutbox.Where(value => ids.Contains(value.Id)).ExecuteDeleteAsync();
            await db.TrackingImportBatches.Where(value => value.Id == batch.Id).ExecuteDeleteAsync();
            await db.OrderItems.Where(i=>guardOrders.Contains(i.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(o=>guardOrders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.CustomerTelegramGroups.Where(value => value.Id == group.Id).ExecuteDeleteAsync();
            await db.Customers.Where(value => value.Id == customer.Id).ExecuteDeleteAsync();
        }
    }
}

public class TrackingSenderProxy : DispatchProxy
{
    public int PhotoCalls, Accepted;
    public Func<Task>? AfterAcceptance;
    private async Task<TelegramSendResult> AcceptedResult()
    {
        if(AfterAcceptance is not null)await AfterAcceptance();
        return new TelegramSendResult(true);
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "SendPhotoBytesWithKeyboardAsync")
        {
            PhotoCalls++;
            if (PhotoCalls == 25) return Task.FromResult(new TelegramSendResult(false, "Too Many Requests") { IsRetryable = true, RetryAfterSeconds = 120 });
            Accepted++;
            if(AfterAcceptance is not null)return AcceptedResult();
        }
        return Task.FromResult(new TelegramSendResult(true));
    }
}

public sealed class TrackingHttpHandler : HttpMessageHandler
{
    public int Calls;
    public HttpStatusCode Code = HttpStatusCode.TooManyRequests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(
            "{\"ok\":false,\"description\":\"temporarily rejected\",\"parameters\":{\"retry_after\":120}}", System.Text.Encoding.UTF8, "application/json") });
    }
}
