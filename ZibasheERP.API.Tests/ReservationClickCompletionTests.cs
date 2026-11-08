using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Telegram;

public static class ReservationClickCompletionTests
{
    private static readonly IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> Rows =
        new[] { new[] { new TelegramInlineButton("confirm", "sly:existing-request") } };

    private static Task Publish(TelegramTemporaryMessageCleaner cleaner) =>
        (Task)typeof(TelegramTemporaryMessageCleaner).GetMethod("PublishPendingPromptsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleaner, [CancellationToken.None])!;

    private static (TelegramTemporaryMessageCleaner Cleaner, PromptSenderProxy Sender, PromptClock Clock) Create()
    {
        var sender = DispatchProxy.Create<ITelegramMessageSender, PromptSenderProxy>();
        var clock = new PromptClock();
        return (new(sender, NullLogger<TelegramTemporaryMessageCleaner>.Instance, clock),
            (PromptSenderProxy)(object)sender, clock);
    }

    private static void Queue(TelegramTemporaryMessageCleaner cleaner, string id = "first-click") =>
        cleaner.QueuePrompt("channel", 1, "next step", Rows, "original", Rows,
            photoFileId: "original-photo", callbackQueryId: id);

    public static async Task Run(Action<bool, string> check)
    {
        var (ordered, sender, _) = Create();
        var editing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = false;
        var acknowledgedAfterAcceptance = false;
        sender.Edit = async (_, _, _, _) => { editing.TrySetResult(); await release.Task; accepted = true; return new(true); };
        sender.Acknowledge = (_, _) =>
        {
            acknowledgedAfterAcceptance = accepted;
            return Task.FromResult(new TelegramSendResult(true));
        };
        Queue(ordered);
        check(sender.Calls.IsEmpty && sender.Acknowledgements.IsEmpty,
            "queuing a step does not complete the click before publication");
        var publish = Publish(ordered);
        try
        {
            await editing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(sender.Acknowledgements.IsEmpty, "in-flight Telegram edit keeps its callback open");
        }
        finally { release.TrySetResult(); }
        await publish;
        check(acknowledgedAfterAcceptance, "client callback completes only after Telegram accepted the next-step keyboard");
        await Publish(ordered);
        check(sender.Acknowledgements.Count == 1 && sender.Acknowledgements.Single() is
            { CallbackId: "first-click", Message: "انتخاب شما دریافت شد.", ShowAlert: false },
            "accepted first-click presentation sends one honest receipt acknowledgement");

        var (retry, retrySender, retryClock) = Create();
        var attempts = 0;
        retrySender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(++attempts > 1, "temporary"));
        Queue(retry, "retry-click");
        await Publish(retry);
        check(retrySender.Acknowledgements.IsEmpty, "a transient edit failure cannot finish the click prematurely");
        retryClock.Advance(TimeSpan.FromMilliseconds(250));
        await Publish(retry);
        check(retrySender.Calls.Count == 2 && retrySender.Acknowledgements.Single().CallbackId == "retry-click",
            "the original callback completes after automatic presentation recovery without a second customer click");

        foreach (var acknowledgementFailure in new[] { false, true })
        {
            var (lost, lostSender, _) = Create();
            lostSender.Acknowledge = (_, _) => acknowledgementFailure
                ? Task.FromException<TelegramSendResult>(new HttpRequestException("lost acknowledgement"))
                : Task.FromResult(new TelegramSendResult(false, "expired callback"));
            Queue(lost);
            await Publish(lost);
            await Publish(lost);
            check(lostSender.Calls.Count == 1 && lostSender.Acknowledgements.Count == 1 && lost.IsScheduled("channel", 1),
                "lost callback acknowledgement never republishes a delivered step or replays a reservation");
        }

        var (unchanged, unchangedSender, _) = Create();
        unchangedSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(false, "Bad Request: message is not modified"));
        Queue(unchanged);
        await Publish(unchanged);
        check(unchangedSender.Acknowledgements.Single().Message == "انتخاب شما دریافت شد.",
            "already-applied keyboard still completes the original customer click");

        var (failed, failedSender, failedClock) = Create();
        failedSender.Edit = (_, _, _, _) => Task.FromResult(new TelegramSendResult(false, "unavailable"));
        Queue(failed);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Publish(failed);
            failedClock.Advance(TimeSpan.FromSeconds(1));
        }
        check(failedSender.Calls.Count == 3 && failedSender.Acknowledgements.Single() is { ShowAlert: true, Message: not null },
            "exhausted presentation retries give the customer one explicit error instead of silent success");

        var (cancelled, cancelledSender, _) = Create();
        Queue(cancelled);
        cancelled.Cancel("channel", 1);
        await Publish(cancelled);
        check(cancelledSender.Calls.IsEmpty && cancelledSender.Acknowledgements.IsEmpty,
            "cancelled queued step cannot issue an obsolete success acknowledgement");

        var (bounded, boundedSender, _) = Create();
        boundedSender.Acknowledge = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(true); };
        Queue(bounded);
        await Publish(bounded).WaitAsync(TimeSpan.FromSeconds(3));
        boundedSender.Acknowledge = (_, _) => Task.FromResult(new TelegramSendResult(true));
        Queue(bounded, "next-click");
        await Publish(bounded).WaitAsync(TimeSpan.FromSeconds(2));
        check(boundedSender.Calls.Count == 2 && boundedSender.Acknowledgements.Last().CallbackId == "next-click",
            "a stalled callback acknowledgement releases the post within its one-second budget");

        await VerifyReceiptFeedback(check);
    }

    private static async Task VerifyReceiptFeedback(Action<bool, string> check)
    {
        var (cleaner, sender, _) = Create();
        cleaner.QueuePrompt("-10042", 17, "next step", Rows, "original", Rows,
            photoFileId: "original-photo", callbackQueryId: "channel-click");
        await Publish(cleaner);
        check(sender.Calls.Count == 1 && sender.Acknowledgements.Single() is
            { Message: "انتخاب شما دریافت شد.", ShowAlert: false },
            "channel acknowledgement confirms receipt without promising a client redraw");
    }
}
