using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;

public static class TelegramItemReplayTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var cs = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION") ?? throw new Exception("Isolated SQL required");
        var guard = new SqlConnectionStringBuilder(cs);
        if (guard.InitialCatalog != "ZibasheERPCloudDev" || guard.DataSource != "127.0.0.1,14333") throw new Exception("Wrong database");
        await using var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlServer(cs)).BuildServiceProvider();
        var factory = services.GetRequiredService<IServiceScopeFactory>();
        var dedupe = new TelegramUpdateDeduplicator(factory);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        var start = Random.Shared.NextInt64(6_000_000_000_000, 7_000_000_000_000);
        var updateIds = Enumerable.Range(0, 5).Select(i => start + i).ToArray();
        var auditIds = new List<Guid>();
        var listId = Guid.NewGuid();
        try
        {
            check(await dedupe.TryAcquireAsync(start) && !await dedupe.TryAcquireAsync(start), "same webhook update cannot run concurrently");
            await dedupe.ReleaseAsync(start);
            check(await dedupe.TryAcquireAsync(start), "failure before an item commits remains retryable");
            foreach (var action in new[] { AdminItemAction.Registered, AdminItemAction.Removed, AdminItemAction.VolumeChanged })
            {
                var update = start + (int)action;
                check(await dedupe.TryAcquireAsync(update), $"fresh {action} operation acquires its webhook receipt");
                var audit = new AdminItemActivity { Id = Guid.NewGuid(), SourceUpdateId = update, ActorTelegramUserId = 999, ActorDisplayName = "Replay fixture", Action = action, CreatedAt = DateTime.UtcNow };
                auditIds.Add(audit.Id);
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    db.AdminItemActivities.Add(audit); await db.SaveChangesAsync(); await tx.RollbackAsync();
                }
                db.ChangeTracker.Clear();
                await dedupe.ReleaseAsync(update);
                check(await dedupe.TryAcquireAsync(update), $"rolled-back {action} has no committed effect and may retry");
                db.AdminItemActivities.Add(audit); await db.SaveChangesAsync();
                await dedupe.ReleaseAsync(update);
                check(!await new TelegramUpdateDeduplicator(factory).TryAcquireAsync(update), $"post-commit Telegram failure cannot replay {action}, including after process restart");
                await db.TelegramProcessedUpdates.Where(u => u.UpdateId == update).ExecuteDeleteAsync();
                check(!await dedupe.TryAcquireAsync(update), $"durable {action} audit still blocks replay after webhook receipt cleanup");
            }
            var tracking = new AdminItemActivity { Id = Guid.NewGuid(), SourceUpdateId = start + 4, ActorTelegramUserId = 999, Action = AdminItemAction.TrackingStarted, CreatedAt = DateTime.UtcNow };
            auditIds.Add(tracking.Id); db.AdminItemActivities.Add(tracking); await db.SaveChangesAsync();
            check(await dedupe.TryAcquireAsync(start + 4), "starting activity tracking alone does not count as a completed item operation");
            await dedupe.ReleaseAsync(start + 4);
            check(await dedupe.TryAcquireAsync(start + 4), "tracking-only update can retry without suppressing an unfinished registration");

            var perfume = await db.Perfumes.Select(p => p.Id).FirstAsync();
            var list = new SalesList { Id = listId, PublicCode = Random.Shared.Next(10000000, 20000000), PerfumeId = perfume, EnglishName = "Replay fixture", TotalVolume = 100, PricePerMl = 1000, Status = SalesListStatus.Open, CreatedAt = DateTime.UtcNow };
            db.SalesLists.Add(list); await db.SaveChangesAsync();
            const long actor = 999;
            var drafts = new TelegramAdminRequestDraftStore();
            var draft = new TelegramAdminRequestDraft { ChatId = actor, UserId = actor, Kind = TelegramAdminRequestKind.CustomRequest, Stage = TelegramAdminRequestStage.AwaitingConfirmation, SalesListId = listId, Identity = "@replay_" + Guid.NewGuid().ToString("N")[..12], VolumeMl = 5, IsBottleOwner = true };
            drafts.Set(draft);
            var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
            var proxy = (PromptSenderProxy)(object)sender;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            proxy.Acknowledge = async (a, _) =>
            {
                if (a.Message == "درخواست ثبت شد ✅") { entered.TrySetResult(); await release.Task; throw new HttpRequestException("Synthetic lost acknowledgement after commit"); }
                return new TelegramSendResult(true);
            };
            var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            void Set(string name, object value) => typeof(TelegramWebhookController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
            Set("_db", db); Set("_adminRequestDrafts", drafts); Set("_sender", sender);
            Set("_salesListRepository", new SalesListRepository(db)); Set("_salesListRequestRepository", new SalesListRequestRepository(db));
            Set("_options", new TelegramOptions { OwnerUserId = actor.ToString() });
            var callback = new TelegramCallbackQuery("replayed", new(actor, null), new TelegramMessage(null, new(actor, "private"), new(actor, null), null, MessageId: 100), "adminrequest:confirm");
            Task Confirm() => (Task)typeof(TelegramWebhookController).GetMethod("ConfirmAdminRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, [callback, CancellationToken.None])!;
            var first = Confirm();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                check(!drafts.TryGet(actor, actor, out _), "successful admin item consumes its draft before a Telegram acknowledgement can fail");
                var repeatedClick = Confirm(); release.TrySetResult();
                try { await first; } catch (HttpRequestException) { }
                await repeatedClick;
                await Confirm();
                check(await db.SalesListRequests.CountAsync(r => r.SalesListId == listId && r.Status == SalesListRequestStatus.Confirmed) == 1 &&
                    await db.SalesLists.Where(l => l.Id == listId).Select(l => l.ReservedVolume).SingleAsync() == 5,
                    "concurrent confirmation and webhook retry after a lost acknowledgement create one admin item and reserve its volume once");
            }
            finally { release.TrySetResult(); }
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.SalesListRequests.Where(r => r.SalesListId == listId).ExecuteDeleteAsync();
            await db.SalesLists.Where(l => l.Id == listId).ExecuteDeleteAsync();
            await db.AdminItemActivities.Where(a => auditIds.Contains(a.Id)).ExecuteDeleteAsync();
            await db.TelegramProcessedUpdates.Where(u => updateIds.Contains(u.UpdateId)).ExecuteDeleteAsync();
        }
    }
}
