using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;

public static class ChannelKeyboardFlowTests
{
    private static MethodInfo Method(string name) => typeof(TelegramWebhookController)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
    private static Task Publish(TelegramTemporaryMessageCleaner cleaner) => (Task)typeof(TelegramTemporaryMessageCleaner)
        .GetMethod("PublishPendingPromptsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(cleaner, [CancellationToken.None])!;
    private static TelegramWebhookController Controller(TelegramTemporaryMessageCleaner cleaner, ITelegramMessageSender sender)
    {
        var controller = (TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        typeof(TelegramWebhookController).GetField("_temporaryMessageCleaner", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, cleaner);
        typeof(TelegramWebhookController).GetField("_sender", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, sender);
        return controller;
    }

    public static async Task Run(Action<bool, string> check)
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var proxy = (PromptSenderProxy)(object)sender;
        var clock = new PromptClock();
        using var cleaner = new TelegramTemporaryMessageCleaner(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance, clock);
        var controller = Controller(cleaner, sender);
        var inventory = new SalesList { Id = Guid.NewGuid(), InventoryCode = 1234,
            IsInventoryOffer = true, TotalVolume = 1.5m, MinimumRequestVolumeMl = 1.5m,
            Status = SalesListStatus.Open };
        var inventoryButtons = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)Method("BuildChannelVolumeButtons")
            .Invoke(null, [inventory, null])!;
        foreach (var username in new string?[] { null, "", " " })
        {
            var missingUsername = new TelegramCallbackQuery("inventory-no-username", new TelegramUser(7, username),
                new TelegramMessage(null, new TelegramChat(-10042, "channel"), null, null, MessageId: 17),
                inventoryButtons.Single().Single().CallbackData);
            var handled = await (Task<bool>)Method("TryHandleChannelSalesListCallbackAsync")
                .Invoke(controller, [missingUsername, CancellationToken.None])!;
            check(handled && proxy.Acknowledgements.Last() is { ShowAlert: true } rejectedInventory &&
                rejectedInventory.Message!.Contains("یوزرنیم"),
                "inventory channel rejects missing username before any database access, reservation or membership lookup");
        }
        while (proxy.Acknowledgements.TryDequeue(out _)) { }
        var response = new CompletionResponseFeature();
        var features = new FeatureCollection();features.Set<IHttpResponseFeature>(response);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext(features) };
        var request = new SalesListRequest
        {
            Id = Guid.NewGuid(), TelegramUserId = "7", TelegramUsername = "buyer", VolumeMl = 3,
            PerfumePricePerMl = 500, BottlePrice = 250, BottleId = Guid.NewGuid(),
            Status = SalesListRequestStatus.PendingConfirmation, ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            SalesList = new SalesList { Id = Guid.NewGuid(), PublicCode = 7628, TotalVolume = 100,
                Status = SalesListStatus.Open, TelegramChannelId = "-10042", TelegramMessageId = 17,
                TelegramPhotoFileId = "original-photo" }
        };
        request.SalesListId = request.SalesList.Id;
        var rows = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)Method("BuildChannelConfirmationButtons")
            .Invoke(null, [request, "simple bottle — 250 تومان"])!;
        var buttons = rows.SelectMany(r => r).ToArray();
        check(buttons.Any(b => b.Text.Contains("@buyer") && b.Text.Contains("3")) &&
            buttons.Any(b => b.Text.Contains("simple bottle")) &&
            buttons.Any(b => b.Text.Contains(1750m.ToString("N0"))),
            "confirmation keyboard visibly includes the customer, volume, chosen bottle and correct total");
        var info = buttons.Where(b => b.CallbackData?.StartsWith("sli:") == true).ToArray();
        check(info.Length == 3 && info.All(b => b.CallbackData == "sli:" + TelegramCallbackParser.EncodeGuid(request.Id)),
            "informational keyboard rows read the same pending request without confirming it");
        check(buttons.Single(b => b.Style == "success").CallbackData == "sly:" + TelegramCallbackParser.EncodeGuid(request.Id) &&
            buttons.Single(b => b.Style == "danger").CallbackData == "sln:" + TelegramCallbackParser.EncodeGuid(request.Id),
            "confirmation and cancellation retain their existing guarded reservation callbacks");
        request.IsGift = true;request.GiftRecipientTelegramUsername = "friend";
        var giftRows = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)Method("BuildChannelConfirmationButtons")
            .Invoke(null, [request, "bottle"])!;
        check(giftRows.SelectMany(r => r).Any(b => b.Text.Contains("@friend")),
            "keyboard-only confirmation still identifies a gift recipient");
        request.IsGift = false;

        // No repository is installed: this real controller path must not fetch a replacement caption.
        await (Task)Method("EditRequestPromptAsync").Invoke(controller, [request, rows, "first-click", null])!;
        check(proxy.Calls.IsEmpty && proxy.Acknowledgements.IsEmpty,
            "webhook queues its next keyboard without editing a photo or acknowledging early");
        await response.CompleteAsync();
        await Publish(cleaner);
        check(proxy.Calls.Single().KeyboardOnly && proxy.Calls.Single().Photo is null &&
            proxy.Calls.Single().Rows.SelectMany(r => r).Single(b => b.Style == "success").CallbackData ==
                "sly:" + TelegramCallbackParser.EncodeGuid(request.Id),
            "first click sends one reply-markup edit on the original channel post without changing its photo or caption");
        check(proxy.Acknowledgements.Single() is { Message: "انتخاب شما دریافت شد.", ShowAlert: false },
            "channel keyboard completion gives honest receipt feedback without claiming that the phone redrew");
        clock.Advance(TimeSpan.FromSeconds(21));
        await (Task)typeof(TelegramTemporaryMessageCleaner).GetMethod("RestoreDueAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cleaner, [clock.GetUtcNow().UtcDateTime, CancellationToken.None])!;
        check(proxy.Calls.Count == 2 && proxy.Calls.All(c => c.KeyboardOnly) &&
            proxy.Calls.Last().Rows.SelectMany(r => r).Any(b => b.CallbackData?.StartsWith("slv:") == true),
            "selection timeout restores volume buttons without refreshing or overwriting the perfume caption");
        var attempts = 0;
        proxy.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(++attempts > 1, "temporary"));
        await (Task)Method("EditRequestPromptAsync").Invoke(controller, [request, rows, "retry-keyboard", null])!;
        await response.CompleteAsync();
        var acknowledgementsBefore = proxy.Acknowledgements.Count;
        await Publish(cleaner);
        check(proxy.Acknowledgements.Count == acknowledgementsBefore,
            "a failed first keyboard edit cannot produce premature receipt feedback");
        clock.Advance(TimeSpan.FromMilliseconds(250));await Publish(cleaner);
        check(attempts == 2 && proxy.Calls.All(c => c.KeyboardOnly) &&
            proxy.Acknowledgements.Last().CallbackId == "retry-keyboard",
            "first keyboard selection automatically recovers from a failed Telegram edit without a second click");
        proxy.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(true));

        var repository = DispatchProxy.Create<ISalesListRequestRepository, KeyboardRequestRepository>();
        ((KeyboardRequestRepository)(object)repository).Request = request;
        typeof(TelegramWebhookController).GetField("_salesListRequestRepository", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(controller, repository);
        var callback = new TelegramCallbackQuery("details", new TelegramUser(7, "buyer"),
            new TelegramMessage(null, new TelegramChat(-10042, "channel"), null, null, MessageId: 17),
            "sli:" + TelegramCallbackParser.EncodeGuid(request.Id));
        await (Task)Method("ShowChannelReservationDetailsAsync").Invoke(controller, [callback, request.Id, CancellationToken.None])!;
        check(proxy.Acknowledgements.Last() is { ShowAlert: true } alert &&
            alert.Message!.Contains(1750m.ToString("N0")) && alert.Message.Length <= 200,
            "tapping amount details displays an exact price alert without creating or confirming an item");
        var before = proxy.Acknowledgements.Count;
        var rejected = false;
        try { await (Task)Method("ShowChannelReservationDetailsAsync").Invoke(controller,
            [callback with { From = new TelegramUser(8, "other") }, request.Id, CancellationToken.None])!; }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected && proxy.Acknowledgements.Count == before,
            "another customer cannot inspect or act on the pending request's detail buttons");
        request.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);rejected = false;
        try { await (Task)Method("ShowChannelReservationDetailsAsync").Invoke(controller, [callback, request.Id, CancellationToken.None])!; }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "expired detail buttons cannot present a stale confirmation");
    }
}

public class KeyboardRequestRepository : DispatchProxy
{
    public SalesListRequest Request = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "GetAsync"
        ? Task.FromResult<SalesListRequest?>(Request) : throw new InvalidOperationException("Unexpected write: " + method.Name);
}

public sealed class CompletionResponseFeature : IHttpResponseFeature
{
    private readonly List<Func<Task>> _completed = new();
    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = new MemoryStream();
    public bool HasStarted => false;
    public void OnStarting(Func<object, Task> callback, object state) { }
    public void OnCompleted(Func<object, Task> callback, object state) => _completed.Add(() => callback(state));
    public async Task CompleteAsync() { foreach (var callback in _completed) await callback(); _completed.Clear(); }
}
