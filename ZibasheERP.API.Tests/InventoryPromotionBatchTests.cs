using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Inventory;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class InventoryPromotionBatchTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var sql = new SqlConnectionStringBuilder(connection);
        if (sql.InitialCatalog != "ZibasheERPCloudDev" || sql.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Isolated development database required");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner = 771234567;
        const string channel = "-1003456087123";
        var used = (await db.SalesLists.Where(l => l.InventoryCode != null).Select(l => l.InventoryCode!.Value).ToArrayAsync()).ToHashSet();
        var codes = Enumerable.Range(1000, 9000).Where(code => !used.Contains(code)).Take(51).ToArray();
        var highest = await db.SalesLists.MaxAsync(l => (int?)l.PublicCode) ?? 0;
        var perfume = await db.Perfumes.Select(p => p.Id).FirstAsync();
        var now = DateTime.UtcNow;
        var lists = codes.Select((code, index) => new SalesList
        {
            Id = Guid.NewGuid(), PerfumeId = perfume, IsInventoryOffer = true, PublicCode = highest + index + 1,
            InventoryCode = code, PersianName = "Test stock " + code, CreatedAt = now.AddSeconds(-index),
            OpenDate = now, Status = SalesListStatus.Open, TotalVolume = 2, MinimumRequestVolumeMl = 2,
            PricePerMl = 50000, FixedBottlePrice = 15000, TelegramChannelId = channel,
            TelegramMessageId = 1000L + index, TelegramPhotoFileId = "photo-" + code
        }).ToArray();
        var ids = lists.Select(l => l.Id).ToArray();
        var lastListId = lists[^1].Id;
        db.AddRange(lists);
        await db.SaveChangesAsync();
        try
        {
            const BindingFlags privateMember = BindingFlags.Instance | BindingFlags.NonPublic;
            var sender = DispatchProxy.Create<ITelegramMessageSender, InventoryPromotionCapture>();
            var capture = (InventoryPromotionCapture)(object)sender;
            var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            foreach (var field in typeof(TelegramWebhookController).GetFields(privateMember)
                        .Where(f => f.Name.EndsWith("Drafts") && f.FieldType.GetConstructor(Type.EmptyTypes) is not null))
                field.SetValue(controller, Activator.CreateInstance(field.FieldType));
            foreach (var (name, value) in new (string, object)[] { ("_db", db), ("_sender", sender),
                         ("_options", new TelegramOptions { OwnerUserId = owner.ToString(), ReadyInventoryChannelId = channel }),
                         ("_logger", NullLogger<TelegramWebhookController>.Instance) })
                typeof(TelegramWebhookController).GetField(name, privateMember)!.SetValue(controller, value);
            TelegramMessage Message(string? text, long id) => new(text, new(owner, "private"), new(owner, null), null, MessageId: id);
            Task Callback(string data, long id) => (Task)typeof(TelegramWebhookController)
                .GetMethod("HandleFlashCallbackAsync", privateMember)!.Invoke(controller,
                    [new TelegramCallbackQuery("fixture", new(owner, null), Message(null, id), data), CancellationToken.None])!;
            Task Input(string text) => (Task)typeof(TelegramWebhookController).GetMethod("TryHandleFlashMessageAsync", privateMember)!
                .Invoke(controller, [Message(text, 501), CancellationToken.None])!;
            string Button(string suffix) => capture.Buttons.Single(b => b.CallbackData?.EndsWith(suffix) == true).CallbackData!;
            int Picks() => capture.Buttons.Count(b => b.CallbackData?.Contains(":pick:") == true);
            await Callback("invoiceadmin:flash:batch", 1);
            check(Picks() == 50 && capture.LastPhoto is [0xff, 0xd8, ..] && capture.Buttons.Any(b => b.Text.Contains("115,000")),
                "batch promotion builds a photo sheet with exactly fifty priced inventory buttons");
            await Callback(Button($":pick:{codes[0]}"), capture.LastMessageId);
            check(capture.Buttons.Any(b => b.Text.StartsWith("✅ 1 ·")), "a checked inventory item is visible immediately without sending another sheet");
            await Callback(Button(":page:1"), capture.LastMessageId);
            check(Picks() == 1 && capture.Buttons.Any(b => b.Text == "ادامه با 1 انتخاب"), "inventory pages keep selection when moving to the second page");
            await Callback(Button($":pick:{codes[^1]}"), capture.LastMessageId);
            await Callback(Button(":next"), capture.LastMessageId);
            await Input("۲۰٪");
            await Input("۲۴ ساعت");
            var apply = Button(":apply");
            var confirmationMessage = capture.LastMessageId;
            check(capture.Texts.Any(text => text.Contains("115,000 ← 95,000") && text.Contains("24 ساعت")) &&
                !await db.FlashSalePromotions.AnyAsync(p => ids.Contains(p.SalesListId)),
                "batch preview shows exact current and discounted parcel totals before activation");

            await db.SalesLists.Where(l => l.Id == lastListId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, SalesListStatus.Full));
            await Callback(apply, confirmationMessage);
            check(!await db.FlashSalePromotions.AnyAsync(p => ids.Contains(p.SalesListId)) &&
                capture.Texts.Last().Contains(codes[^1].ToString()), "a sold or reserved selection aborts the entire batch");
            await db.SalesLists.Where(l => l.Id == lastListId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, SalesListStatus.Open));
            await Callback(apply, confirmationMessage);
            await Callback(apply, confirmationMessage);
            var offers = await db.FlashSalePromotions.AsNoTracking().Where(p => ids.Contains(p.SalesListId)).ToArrayAsync();
            check(offers.Length == 2 && offers.All(p => p.DiscountPercent == 20 && p.EndsAtUtc - p.StartsAtUtc == TimeSpan.FromHours(24)) &&
                offers.Select(p => p.StartsAtUtc).Distinct().Count() == 1,
                "confirmed selections across two pages share a single 24-hour window and replay does not duplicate them");
            var offer = offers.Single(p => p.SalesListId == lists[0].Id);
            var atStart = await FlashSalePricing.QuoteAsync(db, lists[0], offer.StartsAtUtc);
            var expired = await FlashSalePricing.QuoteAsync(db, lists[0], offer.EndsAtUtc);
            check(atStart.PricePerMl == 40000 && expired.PricePerMl == 50000 &&
                ReadyInventoryPresentation.Caption(lists[0], atStart).Contains("مبلغ پروموشن: 95,000 تومان") &&
                !ReadyInventoryPresentation.Caption(lists[0], expired).Contains("مبلغ پروموشن"),
                "checkout and channel caption use the exact discount and restore the original price at expiry");
            var state = FlashSaleMainPriceDisplay.Snapshot(lists[0], atStart);
            check((await FlashSaleMainPriceDisplay.TargetsAsync(db, offer.StartsAtUtc, default)).Contains(lists[0].Id),
                "active batch offers refresh the original inventory channel posts");
            db.NotificationOutbox.Add(new() { Id = FlashSaleMainPriceDisplay.Key(lists[0].Id), Channel = FlashSaleMainPriceDisplay.Channel,
                Recipient = lists[0].Id.ToString("N"), Payload = JsonSerializer.Serialize(state) });
            await db.SaveChangesAsync();
            check((await FlashSaleMainPriceDisplay.TargetsAsync(db, offer.EndsAtUtc, default)).Contains(lists[0].Id),
                "expired inventory promotions are scheduled for caption cleanup");
        }
        finally
        {
            db.ChangeTracker.Clear();
            var marker = FlashSaleMainPriceDisplay.Key(lists[0].Id);
            await db.NotificationOutbox.Where(n => n.Id == marker).ExecuteDeleteAsync();
            await db.FlashSalePromotions.Where(p => ids.Contains(p.SalesListId)).ExecuteDeleteAsync();
            await db.SalesLists.Where(l => ids.Contains(l.Id)).ExecuteDeleteAsync();
        }
    }
}

public class InventoryPromotionCapture : DispatchProxy
{
    private static readonly byte[] Thumbnail = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+nm8sAAAAASUVORK5CYII=");
    public List<string> Texts { get; } = [];
    public TelegramInlineButton[] Buttons { get; private set; } = [];
    public byte[]? LastPhoto { get; private set; }
    public long LastMessageId { get; private set; } = 100;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "DownloadFileAsync") return Task.FromResult(new TelegramFileDownloadResult(true, Thumbnail));
        var name = method.Name;
        var textIndex = name switch
        {
            "SendPhotoBytesWithKeyboardAsync" => 3,
            "SendPhotoWithKeyboardAsync" => 2,
            "SendInlineKeyboardAsync" or "SendAsync" => 1,
            _ => -1
        };
        if (textIndex >= 0) Texts.Add((string)args![textIndex]!);
        if (name == "SendPhotoBytesWithKeyboardAsync") LastPhoto = (byte[])args![1]!;
        if (name is "SendPhotoBytesWithKeyboardAsync" or "SendPhotoWithKeyboardAsync" or "SendInlineKeyboardAsync" or "EditReplyMarkupAsync")
        {
            Buttons = ((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args![textIndex < 0 ? 2 : textIndex + 1]!)
                .SelectMany(row => row).ToArray();
        }
        if (textIndex >= 0) LastMessageId++;
        return Task.FromResult(new TelegramSendResult(true, MessageId: LastMessageId, ExternalFileId: "sheet-file"));
    }
}
