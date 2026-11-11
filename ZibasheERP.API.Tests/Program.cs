using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;

if (args.Contains("--initialize-test-database"))
{
    await IsolatedTestDatabase.InitializeAsync();
    return;
}

static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
var priceController=typeof(ZibasheERP.API.Controllers.TelegramWebhookController);
var pricingInput=new object?[]{"۳۳۹۷۶۸\n۱۰۰\n۵",null,null,null,null};
Check((bool)priceController.GetMethod("TryParseSalesListPricing",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,pricingInput)!&&
    (decimal)pricingInput[1]! ==340000m&&(int)pricingInput[2]! ==100&&(int)pricingInput[3]! ==5,
    "sales-list price input rounds upward before preview while preserving volume and minimum");
Check((decimal)priceController.GetMethod("AdjustedPrice",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[339768m,0m])! == 340000m&&
    (decimal)priceController.GetMethod("AdjustedPrice",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[1000m,0.0001m])! == 2000m,
    "bulk percentage pricing always rounds upward without discarding a fractional-toman increase");
if(args.Contains("--production-recipient-totals"))
{
    ProductionGiftGroupingTests.Run(Check);
    Console.WriteLine("Production recipient total tests passed.");
    return;
}
if(args.Contains("--partial-invoice-cancellation"))
{
    await PartialInvoiceCancellationTests.Run(Check);
    return;
}
if(args.Contains("--tracking-address-notices"))
{
    await ShippingTrackingNoticeTests.Run(Check);
    await ShippingNameWarningTests.Run(Check);
    await ShippingReferenceTests.Run(Check);
    await TrackingRetryTests.Run(Check);
    Console.WriteLine("Tracking address notice tests passed.");
    return;
}
if(args.Contains("--manual-tracking-owner"))
{
    await ShippingReferenceTests.Run(Check);
    await TrackingAddressNameTests.Run(Check);
    await TrackingRetryTests.Run(Check);
    Console.WriteLine("Manual tracking owner tests passed.");
    return;
}
if(args.Contains("--tracking-photo-optional"))
{
    await ShippingReferenceTests.Run(Check);
    await ShippingPackageTests.Run(Check);
    await SharedShippingTests.Run(Check);
    await TrackingRetryTests.Run(Check);
    Console.WriteLine("Optional tracking photo tests passed.");
    return;
}
if(args.Contains("--gift-parcels"))
{
    await GiftParcelMembershipTests.Run(Check);
    await ShippingPackageTests.Run(Check);
    Console.WriteLine("Gift parcel tests passed.");
    return;
}
if(args.Contains("--tracking-addresses"))
{
    await TrackingAddressNameTests.Run(Check);
    await TrackingMatchingTests.Run(Check);
    Console.WriteLine("Tracking address tests passed.");
    return;
}
if(args.Contains("--legacy-pack-photos"))
{
    await LegacyPackPhotoTests.Run(Check);
    Console.WriteLine("Legacy pack photo tests passed.");
    return;
}
if(args.Contains("--dirham-cost"))
{
    await PurchaseCostTests.Run(Check);
    Console.WriteLine("Dirham cost tests passed.");
    return;
}
if(args.Contains("--purchase-report"))
{
    await PerfumePurchaseReportTests.Run(Check);
    Console.WriteLine("Purchase report tests passed.");
    return;
}
if(args.Contains("--ready-inventory"))
{
    await ReadyInventoryTests.Run(Check);
    Console.WriteLine("Ready inventory tests passed.");
    return;
}
if(args.Contains("--group-chat-reply"))
{
    await GroupChatReplyTests.Run(Check);
    Console.WriteLine("Group chat reply tests passed.");
    return;
}
if(args.Contains("--shared-shipping"))
{
    await SharedShippingTests.Run(Check);
    Console.WriteLine("Shared shipping tests passed.");
    return;
}
static Task Restore(TelegramTemporaryMessageCleaner cleaner) => (Task)typeof(TelegramTemporaryMessageCleaner)
    .GetMethod("RestoreDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleaner, [DateTime.UtcNow.AddMinutes(1), CancellationToken.None])!;
static Task Publish(TelegramTemporaryMessageCleaner cleaner) => (Task)typeof(TelegramTemporaryMessageCleaner)
    .GetMethod("PublishPendingPromptsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleaner, [CancellationToken.None])!;
IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows = new[] { new[] { new TelegramInlineButton("test", "test") } };
var sender = DispatchProxy.Create<ITelegramMessageSender, SenderProxy>();
var proxy = (SenderProxy)(object)sender;
var cleaner = new TelegramTemporaryMessageCleaner(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
await cleaner.UpdatePromptAsync("chat", 1, "bottle", rows, "original", rows, TimeSpan.FromSeconds(7), default);
var editCalled = false;
var refresh = await cleaner.UpdatePostAsync("chat", 1, () => { editCalled = true; return Task.FromResult(new TelegramSendResult(true)); }, default);
Check(!editCalled && !refresh.IsSuccessful, "main refresh cannot replace active selection");
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
proxy.Edit = async caption => { if (caption == "original") { entered.SetResult(); await release.Task; } return new(true); };
var restoring = Restore(cleaner);
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var next = cleaner.UpdatePromptAsync("chat", 1, "confirmation", rows, "new original", rows, TimeSpan.FromSeconds(7), default);
Check(!next.IsCompleted, "next prompt waits for in-flight old restore");
release.SetResult();
await restoring;
await next;
Check(proxy.Captions.Last() == "confirmation" && cleaner.IsScheduled("chat", 1), "new prompt survives old restore completion");
proxy.Edit = _ => Task.FromResult(new TelegramSendResult(false, "temporary failure"));
await Restore(cleaner);
Check(cleaner.IsScheduled("chat", 1), "failed restore remains retryable");
proxy.Edit = _ => Task.FromResult(new TelegramSendResult(false, "Bad Request: message is not modified"));
await Restore(cleaner);
Check(!cleaner.IsScheduled("chat", 1), "unchanged restore completes successfully");
await cleaner.UpdatePromptAsync("chat", 1, "again", rows, "old", rows, TimeSpan.FromSeconds(7), default);
cleaner.Cancel("chat", 1);
var before = proxy.Captions.Count;
await Restore(cleaner);
Check(proxy.Captions.Count == before, "cancelled restore cannot overwrite completed order");
Check(cleaner.TryAcquireInteraction("chat", 1, 10, TimeSpan.FromSeconds(20)), "first customer acquires post");
Check(!cleaner.TryAcquireInteraction("chat", 1, 11, TimeSpan.FromSeconds(20)), "another customer cannot interrupt selection");
cleaner.ReleaseInteraction("chat", 1, 11);
Check(!cleaner.TryAcquireInteraction("chat", 1, 11, TimeSpan.FromSeconds(20)), "other customer cannot release owner lock");
cleaner.ReleaseInteraction("chat", 1, 10);
Check(cleaner.TryAcquireInteraction("chat", 1, 11, TimeSpan.FromSeconds(20)), "post available after owner releases");

var keyboardSender = DispatchProxy.Create<ITelegramMessageSender, SenderProxy>();
var keyboardProxy = (SenderProxy)(object)keyboardSender;
var keyboardCleaner = new TelegramTemporaryMessageCleaner(keyboardSender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
await keyboardCleaner.UpdatePromptAsync("channel", 2, "choose", rows, "perfume", rows, TimeSpan.FromSeconds(20), default, keyboardOnly: true);
Check(keyboardProxy.Captions.Count == 0 && keyboardProxy.KeyboardEdits == 1, "selection changes keyboard without replacing perfume caption");
await Restore(keyboardCleaner);
Check(keyboardProxy.Captions.Count == 0 && keyboardProxy.KeyboardEdits == 2, "selection timeout restores keyboard without editing caption");
await keyboardCleaner.UpdatePromptAsync("channel", 2, "confirm amount", rows, "perfume", rows, TimeSpan.FromSeconds(20), default);
Check(keyboardProxy.Captions.Last() == "confirm amount", "confirmation retains full amount details in caption");
await keyboardCleaner.UpdatePromptAsync("channel", 2, "choose again", rows, "perfume", rows, TimeSpan.FromSeconds(20), default, keyboardOnly: true);
Check(keyboardProxy.Captions.Last() == "choose again", "new selection cannot retain stale confirmation caption");
await Restore(keyboardCleaner);
Check(keyboardProxy.Captions.Last() == "perfume", "caption-changing step restores original perfume caption");

await keyboardCleaner.UpdatePromptAsync("channel", 2, "bottles", rows, "perfume", rows, TimeSpan.FromSeconds(20), default, photoFileId: "same-photo");
Check(keyboardProxy.LastPhoto == "same-photo" && keyboardProxy.Captions.Last() == "bottles", "photo refresh retains the original image and displays next step");
await Restore(keyboardCleaner);
Check(keyboardProxy.LastPhoto == "same-photo" && keyboardProxy.Captions.Last() == "perfume", "photo refresh timeout restores the original caption and image");
await keyboardCleaner.UpdatePromptAsync("channel", 2, "confirmation", rows, "perfume", rows, TimeSpan.FromSeconds(20), default, photoFileId: "same-photo");
keyboardCleaner.Cancel("channel", 2);
var mediaBefore = keyboardProxy.Captions.Count;
await Restore(keyboardCleaner);
Check(keyboardProxy.Captions.Count == mediaBefore, "cancelled photo refresh cannot overwrite completed order");

keyboardCleaner.QueuePrompt("channel", 2, "after response", rows, "perfume", rows);
var deferredBefore = keyboardProxy.Captions.Count;
Check(keyboardProxy.Captions.Count == deferredBefore && keyboardCleaner.IsScheduled("channel", 2), "queued prompt protects post without issuing Telegram edit");
var keyboardEditsBefore = keyboardProxy.KeyboardEdits;
await Publish(keyboardCleaner);
Check(keyboardProxy.Captions.Last() == "after response", "background publisher delivers queued step");
Check(keyboardProxy.Captions.Count == deferredBefore + 1 && keyboardProxy.KeyboardEdits == keyboardEditsBefore &&
    keyboardProxy.LastRows!.First().First().Text == rows.First().First().Text &&
    keyboardProxy.LastRows!.First().First().CallbackData == rows.First().First().CallbackData,
    "queued step sends caption and original buttons once without an extra keyboard refresh");
deferredBefore = keyboardProxy.Captions.Count;
await Publish(keyboardCleaner);
Check(keyboardProxy.Captions.Count == deferredBefore && keyboardProxy.KeyboardEdits == keyboardEditsBefore, "published step is not sent again");
keyboardCleaner.QueuePrompt("channel", 2, "old step", rows, "perfume", rows);
keyboardCleaner.QueuePrompt("channel", 2, "new step", rows, "perfume", rows);
await Publish(keyboardCleaner);
Check(keyboardProxy.Captions.Last() == "new step", "new queued step supersedes old step");
keyboardCleaner.QueuePrompt("channel", 2, "cancelled step", rows, "perfume", rows);
keyboardCleaner.Cancel("channel", 2);
deferredBefore = keyboardProxy.Captions.Count;
await Publish(keyboardCleaner);
Check(keyboardProxy.Captions.Count == deferredBefore, "cancelled or confirmed queued step never appears");

var mediaSender = DispatchProxy.Create<ITelegramMessageSender, SenderProxy>();
var mediaProxy = (SenderProxy)(object)mediaSender;
var mediaCleaner = new TelegramTemporaryMessageCleaner(mediaSender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
mediaCleaner.QueuePrompt("channel", 3, "first-click next step", rows, "original perfume", rows, "original-photo");
await Publish(mediaCleaner);
Check(mediaProxy.LastPhoto == "original-photo" && mediaProxy.Captions.SequenceEqual(["first-click next step"]) &&
        mediaProxy.LastRows!.Single().Single().CallbackData == rows.Single().Single().CallbackData,
    "queued first click updates original photo, caption and next-step buttons in one Telegram request");
Check(mediaProxy.KeyboardEdits == 0, "queued photo step never relies on a second keyboard refresh or customer click");
await Restore(mediaCleaner);
Check(mediaProxy.LastPhoto == "original-photo" && mediaProxy.Captions.Last() == "original perfume",
    "queued photo step restores the original photo and perfume caption when selection times out");

await ReservationPromptDeliveryTests.Run(Check);
await CallbackAcknowledgementTests.Run(Check);
await ReservationClickCompletionTests.Run(Check);
await ChannelKeyboardFlowTests.Run(Check);

using var realSender = new TelegramMessageSender(Options.Create(new TelegramOptions { BotToken = "test-token" }));
var handler = new MembershipHandler();
var field = typeof(TelegramMessageSender).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
((HttpClient)field.GetValue(realSender)!).Dispose();
field.SetValue(realSender, new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
Check(await realSender.IsChatMemberAsync("a", "1") && await realSender.IsChatMemberAsync("a", "1") && handler.Calls == 1, "positive membership avoids repeated API request");
await realSender.IsChatMemberAsync("b", "1");
await realSender.IsChatMemberAsync("a", "2");
Check(handler.Calls == 3, "membership cache isolates channel and customer");
handler.Status = "left";
Check(!await realSender.IsChatMemberAsync("a", "3"), "nonmember denied");
handler.Status = "member";
Check(await realSender.IsChatMemberAsync("a", "3") && handler.Calls == 5, "new member is not blocked by cached negative result");
handler.Fail = true;
Check(!await realSender.IsChatMemberAsync("a", "4"), "membership API failure denied");
handler.Fail = false;
Check(await realSender.IsChatMemberAsync("a", "4") && handler.Calls == 7, "membership API errors are not cached");
handler.Status = "member";
Check(!await realSender.IsChatAdministratorAsync("a", "1"), "cached membership never grants administrator permission");
handler.Status = "administrator";
Check(await realSender.IsChatAdministratorAsync("a", "1"), "Telegram administrator is authorized");
handler.Status = "member";
Check(!await realSender.IsChatAdministratorAsync("a", "1"), "removed administrator immediately loses permission");
handler.Fail = true;
Check(!await realSender.IsChatAdministratorAsync("a", "1"), "administrator API failure never grants permission");
var callbackHandler = new CallbackHandler();
((HttpClient)field.GetValue(realSender)!).Dispose();
field.SetValue(realSender, new HttpClient(callbackHandler) { BaseAddress = new Uri("https://example.invalid/") });
Check((await realSender.AnswerCallbackAsync("first-click")).IsSuccessful, "empty callback acknowledgement succeeds");
using (var payload = System.Text.Json.JsonDocument.Parse(callbackHandler.Payload!))
{
    Check(payload.RootElement.GetProperty("text").GetString() == string.Empty, "empty callback never sends JSON null");
    Check(payload.RootElement.GetProperty("cache_time").GetInt32() == 0, "callback response is not cached by client");
    Check(!payload.RootElement.TryGetProperty("url", out _), "ordinary callback never sends a null navigation URL");
}
await realSender.AnswerCallbackAsync("error", "access denied", showAlert: true);
using (var payload = System.Text.Json.JsonDocument.Parse(callbackHandler.Payload!))
    Check(payload.RootElement.GetProperty("text").GetString() == "access denied" && payload.RootElement.GetProperty("show_alert").GetBoolean(), "explicit callback alerts preserved");
var shippingSelectionStore = new TelegramOrderFlowDraftStore();
var firstShippingRequest = Guid.NewGuid();
var nextShippingRequest = Guid.NewGuid();
shippingSelectionStore.ToggleShippingSelection(100, 200, firstShippingRequest);
var reportedShippingSelection = shippingSelectionStore.GetShippingSelectionSnapshot(100, 200);
shippingSelectionStore.ToggleShippingSelection(100, 200, nextShippingRequest);
shippingSelectionStore.ClearShippingSelection(100, 200, reportedShippingSelection);
Check(shippingSelectionStore.GetShippingSelectionSnapshot(100, 200).SetEquals([nextShippingRequest]),
    "shipping report clears reported address selections but preserves later selections");
ProductionGiftGroupingTests.Run(Check);
await DecantListDeliveryTests.Run(Check);
await ArrivalCompletionTests.RunAsync(Check);
await ProductionCopyDeliveryTests.Run(Check);
SmallRemainderReservationTests.Run(Check);
await SmallRemainderReservationTests.RunSqlAsync(Check);
await FlashSaleTests.RunAsync(Check);
InvoicedSalesListReportTests.Run(Check);
AddressReadyItemsTests.Run(Check);
ShippingAddressDisplayTests.Run(Check);
ShippingRecipientInvariantTests.Run(Check);
SharedDecantGroupTests.Run(Check);
SilentGroupTests.Run(Check);
AdminItemActivityTests.Run(Check);
CustomerListReservationTests.Run(Check);
await AccountantReplyTests.Run(Check);
await TrackingMatchingTests.Run(Check);
await TrackingAddressNameTests.Run(Check);
await TrackingRetryTests.Run(Check);
await ShippingPackageTests.Run(Check);
await ShippingTrackingNoticeTests.Run(Check);
await ShippingNameWarningTests.Run(Check);
await PreparedAddressReportTests.Run(Check);
await ManualShippingSentTests.Run(Check);
await GiftParcelMembershipTests.Run(Check);
await SharedShippingTests.Run(Check);
await GroupChatReplyTests.Run(Check);
await PerfumePurchaseReportTests.Run(Check);
await PurchaseCostTests.Run(Check);
await LegacyPackPhotoTests.Run(Check);
await GiftShippingStatisticsTests.Run(Check);
await ShippingPromptIsolationTests.Run(Check);
await ShippingReferenceTests.Run(Check);
await PurchaseAlertPhotoTests.Run(Check);
await AdminMarketingTests.Run(Check);
await HourlyPromotionTests.Run(Check);
await ReadyInventoryTests.Run(Check);
await InvoiceCancellationTests.Run(Check);
await PartialInvoiceCancellationTests.Run(Check);
await InvoiceTransferTests.Run(Check);
await InvoiceBundleTests.Run(Check);
await InvoicePaymentCopyTests.Run(Check);
await BottleUpgradeTests.Run(Check);
await SourcePhotoReadTests.Run(Check);
await InventoryReferencePricingTests.Run(Check);
Console.WriteLine("All API tests passed.");

public class SenderProxy : DispatchProxy
{
    public int KeyboardEdits;
    public IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>? LastRows;
    public string? LastPhoto;
    public List<string> Captions { get; } = new();
    public Func<string, Task<TelegramSendResult>> Edit { get; set; } = _ => Task.FromResult(new TelegramSendResult(true));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "EditReplyMarkupAsync")
        {
            KeyboardEdits++;
            LastRows = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args![2]!;
            return Edit("<keyboard>");
        }
        if (targetMethod.Name == "EditPhotoAsync")
        {
            LastPhoto = (string)args![2]!;
            var mediaCaption = (string)args[3]!;
            LastRows = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[4]!;
            Captions.Add(mediaCaption);
            return Edit(mediaCaption);
        }
        if (targetMethod!.Name != "EditPhotoCaptionAsync") throw new NotSupportedException(targetMethod.Name);
        var caption = (string)args![2]!;
        LastRows = (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!;
        Captions.Add(caption);
        return Edit(caption);
    }
}
public class MembershipHandler : HttpMessageHandler
{
    public int Calls;
    public string Status = "member";
    public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        { Content = new StringContent(Fail ? "{\"ok\":false}" : "{\"ok\":true,\"result\":{\"status\":\"" + Status + "\"}}", System.Text.Encoding.UTF8, "application/json") });
    }
}

public class CallbackHandler : HttpMessageHandler
{
    public string? Payload;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Payload = await request.Content!.ReadAsStringAsync(ct);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", System.Text.Encoding.UTF8, "application/json") };
    }
}
