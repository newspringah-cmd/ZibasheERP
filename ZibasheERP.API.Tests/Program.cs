using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;

static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
static Task Restore(TelegramTemporaryMessageCleaner cleaner) => (Task)typeof(TelegramTemporaryMessageCleaner)
    .GetMethod("RestoreDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleaner, [DateTime.UtcNow.AddMinutes(1), CancellationToken.None])!;
static Task Republish(TelegramTemporaryMessageCleaner cleaner) => (Task)typeof(TelegramTemporaryMessageCleaner)
    .GetMethod("RepublishDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleaner, [DateTime.UtcNow.AddSeconds(3), CancellationToken.None])!;
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

proxy.Edit = _ => Task.FromResult(new TelegramSendResult(true));
await cleaner.UpdatePromptAsync("pulse", 2, "bottles", rows, "original", rows, TimeSpan.FromSeconds(20), default);
var pulseBefore = proxy.Captions.Count;
await Republish(cleaner);
Check(proxy.Captions.Count == pulseBefore + 1 && proxy.Captions.Last().StartsWith("bottles") && proxy.Captions.Last() != "bottles", "one automatic resend produces a fresh edit for the current step");
await Republish(cleaner);
Check(proxy.Captions.Count == pulseBefore + 1, "automatic step resend happens only once");
await cleaner.UpdatePromptAsync("pulse", 2, "old step", rows, "original", rows, TimeSpan.FromSeconds(20), default);
await cleaner.UpdatePromptAsync("pulse", 2, "new step", rows, "original", rows, TimeSpan.FromSeconds(20), default);
await Republish(cleaner);
Check(proxy.Captions.Last().StartsWith("new step"), "resend never restores an obsolete step");
await cleaner.UpdatePromptAsync("pulse", 2, "cancel me", rows, "original", rows, TimeSpan.FromSeconds(20), default);
cleaner.Cancel("pulse", 2);
pulseBefore = proxy.Captions.Count;
await Republish(cleaner);
Check(proxy.Captions.Count == pulseBefore, "confirmation or cancellation suppresses pending step resend");
await cleaner.UpdatePromptAsync("pulse", 2, "expire me", rows, "original", rows, TimeSpan.FromSeconds(1), default);
pulseBefore = proxy.Captions.Count;
await Republish(cleaner);
Check(proxy.Captions.Count == pulseBefore, "expired step is never republished");

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
}
await realSender.AnswerCallbackAsync("error", "access denied", showAlert: true);
using (var payload = System.Text.Json.JsonDocument.Parse(callbackHandler.Payload!))
    Check(payload.RootElement.GetProperty("text").GetString() == "access denied" && payload.RootElement.GetProperty("show_alert").GetBoolean(), "explicit callback alerts preserved");
Console.WriteLine("All reservation interaction tests passed.");

public class SenderProxy : DispatchProxy
{
    public List<string> Captions { get; } = new();
    public Func<string, Task<TelegramSendResult>> Edit { get; set; } = _ => Task.FromResult(new TelegramSendResult(true));
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != "EditPhotoCaptionAsync") throw new NotSupportedException(targetMethod.Name);
        var caption = (string)args![2]!;
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
