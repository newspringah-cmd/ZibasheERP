using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;

public static class CallbackAcknowledgementTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var sender = new TelegramMessageSender(Options.Create(new TelegramOptions { BotToken = "test-token" }));
        var field = typeof(TelegramMessageSender).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(sender)!).Dispose();
        var handler = new DelayedAcknowledgementHandler();
        field.SetValue(sender, new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") });
        var result = await sender.AnswerCallbackAsync("first-click").WaitAsync(TimeSpan.FromSeconds(3));
        check(!result.IsSuccessful && handler.Cancelled,
            "slow spinner acknowledgement finishes within its budget so the accepted selection can continue");

        using var caller = new CancellationTokenSource();
        var cancelled = sender.AnswerCallbackAsync("caller-cancelled", cancellationToken: caller.Token);
        caller.Cancel();
        var propagated = false;
        try { await cancelled; }
        catch (OperationCanceledException) { propagated = true; }
        check(propagated, "acknowledgement budget never swallows cancellation of the webhook itself");

        var alert = sender.AnswerCallbackAsync("explicit-alert", "برای ثبت درخواست باید عضو کانال باشید.", showAlert: true);
        await Task.Delay(1150);
        check(!alert.IsCompleted, "membership and other explicit alerts keep their normal delivery window");
        handler.Release.TrySetResult();
        check((await alert.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccessful, "explicit alert still reaches Telegram after a slow response");
    }
}

public sealed class DelayedAcknowledgementHandler : HttpMessageHandler
{
    public bool Cancelled;
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { await Release.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"ok\":true,\"result\":true}", System.Text.Encoding.UTF8, "application/json") };
    }
}
