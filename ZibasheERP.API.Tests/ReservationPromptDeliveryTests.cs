using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Telegram;

public static class ReservationPromptDeliveryTests
{
    private static Task Publish(TelegramTemporaryMessageCleaner cleaner) =>
        (Task)typeof(TelegramTemporaryMessageCleaner)
            .GetMethod("PublishPendingPromptsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cleaner, [CancellationToken.None])!;

    private static Task Restore(TelegramTemporaryMessageCleaner cleaner, DateTime now) =>
        (Task)typeof(TelegramTemporaryMessageCleaner)
            .GetMethod("RestoreDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cleaner, [now, CancellationToken.None])!;

    private static readonly IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows =
        new[] { new[] { new TelegramInlineButton("confirm", "sly:existing-request") } };

    private static (TelegramTemporaryMessageCleaner Cleaner, PromptSenderProxy Sender, PromptClock Clock) Create()
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var clock = new PromptClock();
        return (new TelegramTemporaryMessageCleaner(sender,
            NullLogger<TelegramTemporaryMessageCleaner>.Instance, clock), (PromptSenderProxy)(object)sender, clock);
    }

    public static async Task Run(Action<bool, string> check)
    {
        var (cleaner, proxy, clock) = Create();
        var attempts = 0;
        proxy.Edit = (_, _, _, _) => Task.FromResult(++attempts == 1
            ? new TelegramSendResult(false, "temporary network failure")
            : new TelegramSendResult(true));
        cleaner.QueuePrompt("channel", 1, "confirm first click", Rows, "original", Rows, "original-photo");
        await Publish(cleaner);
        await Publish(cleaner);
        check(attempts == 1, "transient prompt retry is backed off instead of flooding Telegram");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await Publish(cleaner);
        check(attempts == 2 && proxy.Calls.Last().Caption == "confirm first click" &&
                proxy.Calls.All(call => call.Photo == "original-photo" &&
                    call.Rows.Single().Single().CallbackData == "sly:existing-request"),
            "first click reaches the next step after a transient Telegram failure without another click");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Publish(cleaner);
        check(attempts == 2, "accepted retry does not replay the reservation or send the prompt again");

        var (exceptionCleaner, exceptionSender, exceptionClock) = Create();
        exceptionSender.Edit = (_, _, _, _) => Task.FromException<TelegramSendResult>(new HttpRequestException("temporary"));
        exceptionCleaner.QueuePrompt("channel", 2, "choose bottle", Rows, "original", Rows);
        await Publish(exceptionCleaner);
        exceptionSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(true));
        exceptionClock.Advance(TimeSpan.FromSeconds(1));
        await Publish(exceptionCleaner);
        check(exceptionSender.Calls.Count == 2, "thrown Telegram failure also retains the accepted first-click prompt");

        var (cancelled, cancelledSender, cancelledClock) = Create();
        cancelledSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(false, "temporary"));
        cancelled.QueuePrompt("channel", 3, "do not resurrect", Rows, "original", Rows);
        await Publish(cancelled);
        cancelled.Cancel("channel", 3);
        cancelledClock.Advance(TimeSpan.FromSeconds(1));
        await Publish(cancelled);
        check(cancelledSender.Calls.Count == 1 && !cancelled.IsScheduled("channel", 3),
            "cancelled or completed reservation cannot be resurrected by a pending retry");

        var (limited, limitedSender, limitedClock) = Create();
        limitedSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(false, "unavailable"));
        limited.QueuePrompt("channel", 4, "bounded retries", Rows, "original", Rows);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Publish(limited);
            limitedClock.Advance(TimeSpan.FromSeconds(1));
        }
        check(limitedSender.Calls.Count == 3, "persistent Telegram failure has a bounded automatic retry count");

        var (unchanged, unchangedSender, unchangedClock) = Create();
        unchangedSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(false, "Bad Request: message is not modified"));
        unchanged.QueuePrompt("channel", 5, "already visible", Rows, "original", Rows);
        await Publish(unchanged);
        unchangedClock.Advance(TimeSpan.FromSeconds(1));
        await Publish(unchanged);
        check(unchangedSender.Calls.Count == 1 && unchanged.IsScheduled("channel", 5),
            "already-visible prompt counts as accepted instead of triggering more edits");

        var (visible, visibleSender, visibleClock) = Create();
        check(visible.TryAcquireInteraction("channel", 6, 100, TimeSpan.FromSeconds(20)),
            "customer owns the post before delayed prompt publication");
        visible.QueuePrompt("channel", 6, "late but visible", Rows, "original", Rows);
        visibleClock.Advance(TimeSpan.FromSeconds(10));
        await Publish(visible);
        await Restore(visible, visibleClock.GetUtcNow().UtcDateTime.AddSeconds(15));
        check(visibleSender.Calls.Count == 1, "selection keeps its full timeout after the prompt actually becomes visible");
        visibleClock.Advance(TimeSpan.FromSeconds(15));
        check(!visible.TryAcquireInteraction("channel", 6, 200, TimeSpan.FromSeconds(20)),
            "another customer cannot take a delayed prompt during its visible selection window");
        await Restore(visible, visibleClock.GetUtcNow().UtcDateTime.AddSeconds(6));
        check(visibleSender.Calls.Last().Caption == "original" && !visible.IsScheduled("channel", 6),
            "accepted prompt still restores the original list after the visible-step timeout");
        visibleClock.Advance(TimeSpan.FromSeconds(6));
        check(visible.TryAcquireInteraction("channel", 6, 200, TimeSpan.FromSeconds(20)),
            "other customers regain access after the visible selection window expires");

        var (expired, expiredSender, expiredClock) = Create();
        expired.QueuePrompt("channel", 7, "obsolete", Rows, "original", Rows);
        expiredClock.Advance(TimeSpan.FromSeconds(21));
        await Publish(expired);
        check(expiredSender.Calls.IsEmpty, "expired selection is never published as a stale next step");

        await VerifyIndependentPosts(check);
        await VerifyMaintenanceDoesNotBlockPrompts(check);
        await VerifySupersededRetry(check);
    }

    private static async Task VerifyIndependentPosts(Action<bool, string> check)
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var proxy = (PromptSenderProxy)(object)sender;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Edit = async (_, id, _, ct) =>
        {
            if (id == 1) { blocked.TrySetResult(); await release.Task.WaitAsync(ct); }
            else delivered.TrySetResult();
            return new(true);
        };
        using var cleaner = new TelegramTemporaryMessageCleaner(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
        cleaner.QueuePrompt("channel", 1, "slow network", Rows, "original", Rows);
        await cleaner.StartAsync(default);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cleaner.QueuePrompt("channel", 2, "next customer", Rows, "original", Rows);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!release.Task.IsCompleted, "slow Telegram edit on one post never blocks another customer's first click");
        }
        finally { release.TrySetResult(); await cleaner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task VerifyMaintenanceDoesNotBlockPrompts(Action<bool, string> check)
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var proxy = (PromptSenderProxy)(object)sender;
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Delete = async (_, _, ct) => { deleting.TrySetResult(); await release.Task.WaitAsync(ct); return new(true); };
        proxy.Edit = (_, _, _, _) => { delivered.TrySetResult(); return Task.FromResult(new TelegramSendResult(true)); };
        using var cleaner = new TelegramTemporaryMessageCleaner(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
        cleaner.ScheduleDelete("old private message", 10, TimeSpan.FromSeconds(-1));
        await cleaner.StartAsync(default);
        try
        {
            await deleting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cleaner.QueuePrompt("channel", 11, "first click during maintenance", Rows, "original", Rows);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!release.Task.IsCompleted, "temporary message cleanup cannot stall a new reservation prompt");
        }
        finally { release.TrySetResult(); await cleaner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task VerifySupersededRetry(Action<bool, string> check)
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var proxy = (PromptSenderProxy)(object)sender;
        var editing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Edit = async (_, _, caption, ct) =>
        {
            if (caption == "old step") { editing.TrySetResult(); await release.Task.WaitAsync(ct); return new(false, "temporary"); }
            delivered.TrySetResult(); return new(true);
        };
        using var cleaner = new TelegramTemporaryMessageCleaner(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance);
        cleaner.QueuePrompt("channel", 12, "old step", Rows, "original", Rows);
        await cleaner.StartAsync(default);
        try
        {
            await editing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cleaner.QueuePrompt("channel", 12, "new step", Rows, "original", Rows);
            check(proxy.Calls.Count == 1, "edits to the same post remain serialized");
            release.TrySetResult();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(proxy.Calls.Count == 2 && proxy.Calls.Last().Caption == "new step",
                "failed in-flight edit cannot drop or retry over a newer selection");
        }
        finally { release.TrySetResult(); await cleaner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}

public class PromptSenderProxy : DispatchProxy
{
    public record Call(string ChatId, long Id, string Caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows, string? Photo = null, bool KeyboardOnly = false);
    public ConcurrentQueue<Call> Calls { get; } = new();
    public record Acknowledgement(string CallbackId, string? Message, bool ShowAlert);
    public ConcurrentQueue<Acknowledgement> Acknowledgements { get; } = new();
    public Func<Acknowledgement, CancellationToken, Task<TelegramSendResult>> Acknowledge { get; set; } =
        (_, _) => Task.FromResult(new TelegramSendResult(true));
    public Func<string, long, string, CancellationToken, Task<TelegramSendResult>> Edit { get; set; } =
        (_, _, _, _) => Task.FromResult(new TelegramSendResult(true));
    public Func<string, long, CancellationToken, Task<TelegramSendResult>> Delete { get; set; } =
        (_, _, _) => Task.FromResult(new TelegramSendResult(true));
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "AnswerCallbackAsync")
        {
            var acknowledgement = new Acknowledgement((string)args![0]!, (string?)args[1], (bool)args[3]!);
            Acknowledgements.Enqueue(acknowledgement);
            return Acknowledge(acknowledgement, (CancellationToken)args[2]!);
        }
        if (method!.Name == "DeleteMessageAsync") return Delete((string)args![0]!, (long)args[1]!, (CancellationToken)args[2]!);
        if (method.Name == "EditReplyMarkupAsync")
        {
            var keyboard = new Call((string)args![0]!, (long)args[1]!, "<keyboard>",
                (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[2]!, KeyboardOnly: true);
            Calls.Enqueue(keyboard);
            return Edit(keyboard.ChatId, keyboard.Id, keyboard.Caption, (CancellationToken)args[3]!);
        }
        if (method.Name == "EditPhotoAsync")
        {
            var photoCall = new Call((string)args![0]!, (long)args[1]!, (string)args[3]!,
                (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[4]!, (string)args[2]!);
            Calls.Enqueue(photoCall);
            return Edit(photoCall.ChatId, photoCall.Id, photoCall.Caption, (CancellationToken)args[5]!);
        }
        if (method.Name != "EditPhotoCaptionAsync") throw new NotSupportedException(method.Name);
        var call = new Call((string)args![0]!, (long)args[1]!, (string)args[2]!,
            (IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[3]!);
        Calls.Enqueue(call);
        return Edit(call.ChatId, call.Id, call.Caption, (CancellationToken)args[4]!);
    }
}

public sealed class PromptClock : TimeProvider
{
    private long ticks = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
}
