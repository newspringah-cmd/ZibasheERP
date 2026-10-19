using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;

public static class DecantListDeliveryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons =
            [new[] { new TelegramInlineButton("photo", "decantphoto:list:test") }];
        var listId = Guid.NewGuid();
        var store = new DecantReceiptTestStore();
        var sender = DispatchProxy.Create<ITelegramMessageSender, DecantSenderTestProxy>();
        var proxy = (DecantSenderTestProxy)sender;
        var service = new TelegramDecantListDelivery(store, sender);
        Task<TelegramSendResult> Send(TelegramDecantListDelivery target, string? extra = null) =>
            target.SendAsync(listId, "-1001", "photo", "caption", buttons, extra, default);
        var sent = await Send(service, "continuation");
        check(sent.IsSuccessful && proxy.Calls == 2, "decant delivery records main message and continuation");
        await Send(service, "continuation");
        await Send(new TelegramDecantListDelivery(store, sender), "continuation");
        check(proxy.Calls == 2, "repeated callback and recreated service cannot resend a delivered list");

        store = new DecantReceiptTestStore();
        service = new TelegramDecantListDelivery(store, sender);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Calls = 0;
        proxy.Handler = async _ => { entered.SetResult(); await release.Task; return new(true, MessageId: 51); };
        var first = Send(service);
        await entered.Task;
        var competing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(new TelegramDecantListDelivery(store, sender))));
        check(proxy.Calls == 1 && competing.All(result => !result.IsSuccessful), "concurrent decant requests cannot bypass the persistent delivery claim");
        release.SetResult();
        await first;

        store = new DecantReceiptTestStore();
        service = new TelegramDecantListDelivery(store, sender);
        proxy.Calls = 0;
        proxy.Handler = _ => Task.FromResult(new TelegramSendResult(false, "connection timed out"));
        await Send(service);
        var uncertain = await Send(new TelegramDecantListDelivery(store, sender));
        check(proxy.Calls == 1 && !uncertain.IsSuccessful, "uncertain delivery is held for review instead of automatically sending a duplicate");

        store = new DecantReceiptTestStore { FailNextSave = true };
        service = new TelegramDecantListDelivery(store, sender);
        proxy.Calls = 0;
        proxy.Handler = _ => Task.FromResult(new TelegramSendResult(true, MessageId: 52));
        var failedSave = await Send(service);
        await Send(new TelegramDecantListDelivery(store, sender));
        check(!failedSave.IsSuccessful && failedSave.MessageId == 52 && proxy.Calls == 1,
            "receipt-save failure after successful send cannot trigger a second main message");

        store = new DecantReceiptTestStore();
        service = new TelegramDecantListDelivery(store, sender);
        proxy.Calls = 0;
        proxy.Handler = args => Task.FromResult((string?)args[1] is null
            ? new TelegramSendResult(false, "continuation failed") : new TelegramSendResult(true, MessageId: 53));
        var partial = await Send(service, "continuation");
        await Send(service, "continuation");
        check(partial.MessageId == 53 && !partial.IsSuccessful && proxy.Calls == 2,
            "failed continuation preserves acknowledged main message and blocks a duplicate list");
        check(TelegramDecantListDelivery.CreateKey(listId, " -1001 ") == TelegramDecantListDelivery.CreateKey(listId, "-1001") &&
            TelegramDecantListDelivery.CreateKey(listId, "-1001") != TelegramDecantListDelivery.CreateKey(Guid.NewGuid(), "-1001") &&
            TelegramDecantListDelivery.CreateKey(listId, "-1001") != TelegramDecantListDelivery.CreateKey(listId, "-1002"),
            "delivery identity uses the list cycle and target group rather than the reused display code");

        using var realSender = new TelegramMessageSender(Options.Create(new TelegramOptions { BotToken = "test-token" }));
        var errors = new DecantServerErrorHandler();
        typeof(TelegramMessageSender).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(realSender, new HttpClient(errors) { BaseAddress = new Uri("https://example.invalid/") });
        var errorResult = await realSender.SendDecantListPartAsync("-1001", "photo", "caption", buttons);
        check(!errorResult.IsSuccessful && errors.Calls == 1, "decant HTTP 500 is not blindly retried by the transport");

        var list = new SalesList { Id = listId, PublicCode = 77, EnglishName = "Example", TotalVolume = 10,
            ReservedVolume = 10, ClosedDate = new DateTime(2026, 10, 16, 19, 41, 0) };
        check(SalesListCompletionDate.Format(list, []) == "۱۴۰۵/۰۷/۲۴", "completion date is Persian with Persian digits");
        list.ClosedDate = new DateTime(2026, 10, 16, 21, 0, 0);
        check(SalesListCompletionDate.Format(list, []) == "۱۴۰۵/۰۷/۲۵", "completion date observes midnight in Tehran rather than UTC");
        list.ClosedDate = null;
        check(SalesListCompletionDate.Format(list, []) == "ثبت نشده", "unknown completion date is not replaced with today's date");
        var completedRequest = new SalesListRequest { SalesListId = listId, VolumeMl = 10,
            Status = SalesListRequestStatus.Confirmed, ConfirmedAt = new DateTime(2026, 10, 16, 19, 41, 0) };
        check(SalesListCompletionDate.Format(list, [completedRequest]) == "۱۴۰۵/۰۷/۲۴", "full list without a closed date uses its last confirmed portion");
        list.ClosedDate = new DateTime(2026, 10, 15, 12, 0, 0);
        check(SalesListCompletionDate.Format(list, [completedRequest]) == "۱۴۰۵/۰۷/۲۳", "saved completion date takes precedence over later request corrections");
        list.Requests = [completedRequest];
        var pages = ((string Main, string? Continuation))typeof(TelegramWebhookController)
            .GetMethod("FormatDecantQueueListPages", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [list, list.Requests.ToArray()])!;
        check(pages.Main.StartsWith("📅 <b>تاریخ تکمیل لیست: ۱۴۰۵/۰۷/۲۳</b>"),
            "decant message begins with the bold list completion date");
        list.EnglishName = new string('x', 200);
        list.PersianName = new string('ع', 200);
        list.TopNotes = list.MiddleNotes = list.BaseNotes = new string('ن', 40);
        list.Requests = Enumerable.Range(0, 50).Select(i => new SalesListRequest { SalesListId = listId,
            TelegramUsername = "customer_" + i, VolumeMl = 1, Status = SalesListRequestStatus.Invoiced }).ToArray();
        pages = ((string Main, string? Continuation))typeof(TelegramWebhookController)
            .GetMethod("FormatDecantQueueListPages", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [list, list.Requests.ToArray()])!;
        check(pages.Main.Length <= 1000, "completion heading is included in the photo caption length budget");
    }
}

public sealed class DecantReceiptTestStore : IDecantListDeliveryStore
{
    private readonly ConcurrentDictionary<Guid, DecantListDeliveryReceipt> receipts = new();
    public bool FailNextSave;
    public Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct) =>
        Task.FromResult(receipts.TryAdd(key, new(listId)));
    public Task<DecantListDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct) =>
        Task.FromResult(receipts.TryGetValue(key, out var receipt) ? receipt : null);
    public Task SaveAsync(Guid key, DecantListDeliveryReceipt receipt, CancellationToken ct)
    {
        if (FailNextSave) { FailNextSave = false; throw new InvalidOperationException("test save failure"); }
        receipts[key] = receipt;
        return Task.CompletedTask;
    }
}

public class DecantSenderTestProxy : DispatchProxy
{
    public int Calls;
    public Func<object?[], Task<TelegramSendResult>> Handler = _ => Task.FromResult(new TelegramSendResult(true, MessageId: 50));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != nameof(ITelegramMessageSender.SendDecantListPartAsync)) throw new NotSupportedException();
        Interlocked.Increment(ref Calls);
        return Handler(args!);
    }
}

public class DecantServerErrorHandler : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        { Content = new StringContent("{\"ok\":false,\"description\":\"server error\"}") });
    }
}
