using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.API.Controllers;

internal sealed class PaymentReminderDraft
{
    public required string Bucket { get; init; }
    public required string MessageText { get; set; }
    public bool AwaitingMessageText { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

internal sealed record PaymentReminderCandidate(
    Guid InvoiceId,
    string InvoiceNumber,
    decimal TotalAmount,
    DateTime IssuedAt,
    Guid CustomerId,
    string CustomerName,
    string? CustomerUsername,
    string? DestinationChatId,
    bool HasActiveDestination);

public sealed partial class TelegramWebhookController
{
    private const string DefaultPaymentReminderText =
        "سلام وقت بخیر 🌷\n" +
        "یادآوری می‌شود فاکتورهای زیر همچنان در انتظار پرداخت هستند. " +
        "لطفاً در صورت پرداخت، رسید را در همین گروه ارسال کنید.";

    private static readonly ConcurrentDictionary<(long ChatId, long UserId), PaymentReminderDraft>
        PaymentReminderDrafts = new();

    private static readonly SemaphoreSlim PaymentReminderSendLock = new(1, 1);

    private async Task HandlePaymentReminderCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken ct)
    {
        if (callback.Message is null || string.IsNullOrWhiteSpace(callback.Data))
            return;

        var chatId = callback.Message.Chat.Id;
        var userId = callback.From.Id;
        var data = callback.Data;

        if (data == "paymentreminder:menu")
        {
            PaymentReminderDrafts.TryRemove((chatId, userId), out _);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendPaymentReminderMenuAsync(chatId, ct);
            return;
        }

        if (data.StartsWith("paymentreminder:bucket:", StringComparison.Ordinal))
        {
            var bucket = data["paymentreminder:bucket:".Length..];
            if (!IsPaymentReminderBucket(bucket))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "بازه انتخاب‌شده معتبر نیست.", ct, true);
                return;
            }

            PaymentReminderDrafts[(chatId, userId)] = new PaymentReminderDraft
            {
                Bucket = bucket,
                MessageText = DefaultPaymentReminderText
            };
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendPaymentReminderPreviewAsync(chatId, userId, ct);
            return;
        }

        if (data == "paymentreminder:edit")
        {
            if (!TryGetPaymentReminderDraft(chatId, userId, out var draft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct, true);
                return;
            }

            draft.AwaitingMessageText = true;
            draft.UpdatedAt = DateTime.UtcNow;
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId,
                "متن جدید یادآوری را کامل بفرستید. برای لغو /cancel را ارسال کنید.", ct);
            return;
        }

        if (data == "paymentreminder:cancel")
        {
            PaymentReminderDrafts.TryRemove((chatId, userId), out _);
            await _sender.AnswerCallbackAsync(callback.Id, "لغو شد.", ct);
            await SendPaymentReminderMenuAsync(chatId, ct);
            return;
        }

        if (data == "paymentreminder:send")
        {
            if (!TryGetPaymentReminderDraft(chatId, userId, out var draft) || draft.AwaitingMessageText)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct, true);
                return;
            }

            await _sender.AnswerCallbackAsync(callback.Id, "ارسال شروع شد…", ct);
            await SendPaymentRemindersAsync(chatId, userId, draft, ct);
            return;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct);
    }

    private async Task<bool> TryHandlePaymentReminderMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null ||
            !TryGetPaymentReminderDraft(message.Chat.Id, message.From.Id, out var draft) ||
            !draft.AwaitingMessageText)
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            PaymentReminderDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            return false;
        }

        var text = message.Text?.Trim();
        if (string.Equals(text, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            PaymentReminderDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "ویرایش متن یادآوری لغو شد.", ct);
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(message.Chat.Id, "متن یادآوری نمی‌تواند خالی باشد.", ct);
            return true;
        }

        if (text.Length > 1200)
        {
            await ReplyAsync(message.Chat.Id, "متن یادآوری حداکثر می‌تواند ۱۲۰۰ نویسه باشد.", ct);
            return true;
        }

        draft.MessageText = text;
        draft.AwaitingMessageText = false;
        draft.UpdatedAt = DateTime.UtcNow;
        await SendPaymentReminderPreviewAsync(message.Chat.Id, message.From.Id, ct);
        return true;
    }

    private async Task SendPaymentReminderMenuAsync(long chatId, CancellationToken ct)
    {
        var candidates = await LoadPaymentReminderCandidatesAsync(ct);
        var now = DateTime.UtcNow;
        int Count(string bucket) => candidates.Count(value => IsInPaymentReminderBucket(value.IssuedAt, bucket, now));

        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton($"۴۸ تا ۷۲ ساعت ({Count("h48_72")})", "paymentreminder:bucket:h48_72") },
            new[] { new TelegramInlineButton($"۷ تا ۸ روز ({Count("d7_8")})", "paymentreminder:bucket:d7_8") },
            new[] { new TelegramInlineButton($"۱۴ تا ۱۵ روز ({Count("d14_15")})", "paymentreminder:bucket:d14_15") },
            new[] { new TelegramInlineButton($"بیش از یک ماه ({Count("over30")})", "paymentreminder:bucket:over30") },
            new[] { new TelegramInlineButton("↩ بازگشت", "invoiceadmin:menu:invoices") }
        };

        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "📣 یادآوری فاکتورهای در انتظار پرداخت\n\n" +
            "عدد کنار هر بازه، تعداد فاکتورهایی است که امروز هنوز برایشان یادآوری موفق ارسال نشده است.",
            buttons, ct);
    }

    private async Task SendPaymentReminderPreviewAsync(long chatId, long userId, CancellationToken ct)
    {
        if (!TryGetPaymentReminderDraft(chatId, userId, out var draft))
            return;

        var now = DateTime.UtcNow;
        var candidates = (await LoadPaymentReminderCandidatesAsync(ct))
            .Where(value => IsInPaymentReminderBucket(value.IssuedAt, draft.Bucket, now))
            .OrderBy(value => value.IssuedAt)
            .ToArray();
        var customerGroups = candidates.GroupBy(value => value.CustomerId).ToArray();
        var deliverableGroups = customerGroups
            .Where(group => group.Any(value => value.HasActiveDestination))
            .ToArray();
        var missingGroups = customerGroups.Length - deliverableGroups.Length;
        var missingGroupNames = customerGroups
            .Where(group => group.All(value => !value.HasActiveDestination))
            .Select(group => FormatPaymentReminderCustomer(group.First()))
            .Take(20)
            .ToArray();

        var lines = deliverableGroups.Take(30).Select(group =>
        {
            var first = group.First();
            var identity = FormatPaymentReminderCustomer(first);
            var invoiceNumbers = string.Join("، ", group.Select(value => value.InvoiceNumber));
            var total = group.Sum(value => value.TotalAmount);
            var oldest = FormatPaymentReminderAge(group.Min(value => value.IssuedAt), now);
            return $"• {identity} — {group.Count()} فاکتور — {total:N0} تومان\n  {invoiceNumbers} — {oldest}";
        }).ToList();

        if (deliverableGroups.Length > 30)
            lines.Add($"… و {deliverableGroups.Length - 30} مشتری دیگر");

        var preview =
            $"📋 پیش‌نمایش بازه {PaymentReminderBucketTitle(draft.Bucket)}\n" +
            $"مشتری قابل ارسال: {deliverableGroups.Length}\n" +
            $"فاکتور قابل ارسال: {deliverableGroups.Sum(group => group.Count())}\n" +
            $"بدون گروه فعال: {missingGroups}\n\n" +
            $"متن پیام:\n{draft.MessageText}\n\n" +
            (lines.Count == 0 ? "مورد قابل ارسالی وجود ندارد." : string.Join("\n", lines)) +
            (missingGroupNames.Length == 0
                ? string.Empty
                : "\n\n⚠️ بدون گروه فعال:\n" + string.Join("، ", missingGroupNames));

        foreach (var part in SplitTelegramMessage(preview).SkipLast(1))
            await _sender.SendAsync(chatId.ToString(), part, ct);

        var finalPart = SplitTelegramMessage(preview).Last();
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), finalPart,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✏️ ویرایش متن", "paymentreminder:edit"),
                    new TelegramInlineButton("✅ تأیید و ارسال", "paymentreminder:send")
                },
                new[] { new TelegramInlineButton("❌ لغو", "paymentreminder:cancel") }
            }, ct);
    }

    private async Task SendPaymentRemindersAsync(
        long reportChatId,
        long userId,
        PaymentReminderDraft draft,
        CancellationToken ct)
    {
        if (!await PaymentReminderSendLock.WaitAsync(0, ct))
        {
            await ReplyAsync(reportChatId, "یک ارسال یادآوری دیگر در حال اجراست؛ کمی بعد دوباره تلاش کنید.", ct);
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var candidates = (await LoadPaymentReminderCandidatesAsync(ct, trackEntities: true))
                .Where(value => IsInPaymentReminderBucket(value.IssuedAt, draft.Bucket, now))
                .GroupBy(value => value.CustomerId)
                .ToArray();

            var sentCustomers = 0;
            var sentInvoices = 0;
            var missingGroups = 0;
            var missingGroupNames = new List<string>();
            var failures = new List<string>();

            foreach (var customerInvoices in candidates)
            {
                var first = customerInvoices.First();
                if (!first.HasActiveDestination || string.IsNullOrWhiteSpace(first.DestinationChatId))
                {
                    missingGroups++;
                    missingGroupNames.Add(FormatPaymentReminderCustomer(first));
                    continue;
                }

                var message = BuildCustomerPaymentReminder(draft.MessageText, customerInvoices, now);
                TelegramSendResult result = new(false, "پیامی ارسال نشد.");
                var allPartsSent = true;
                foreach (var part in SplitTelegramMessage(message))
                {
                    result = await _sender.SendAsync(first.DestinationChatId, part, ct);
                    if (result.IsSuccessful)
                        continue;
                    allPartsSent = false;
                    break;
                }

                if (!allPartsSent)
                {
                    failures.Add($"{FormatPaymentReminderCustomer(first)}: {result.Error ?? "خطای نامشخص"}");
                    continue;
                }

                var ids = customerInvoices.Select(value => value.InvoiceId).ToArray();
                var invoices = await _db.Invoices
                    .Where(value => ids.Contains(value.Id))
                    .ToArrayAsync(ct);
                foreach (var invoice in invoices)
                    invoice.LastPaymentReminderSentAt = now;
                var customer = await _db.Customers.FirstAsync(value => value.Id == first.CustomerId, ct);
                customer.LastPaymentReminderSentAt = now;
                await _db.SaveChangesAsync(ct);

                sentCustomers++;
                sentInvoices += invoices.Length;
            }

            PaymentReminderDrafts.TryRemove((reportChatId, userId), out _);
            var report =
                "✅ ارسال یادآوری پرداخت پایان یافت.\n" +
                $"مشتری موفق: {sentCustomers}\n" +
                $"فاکتور موفق: {sentInvoices}\n" +
                $"بدون گروه فعال: {missingGroups}\n" +
                $"ناموفق: {failures.Count}";
            if (missingGroupNames.Count > 0)
                report += "\n\n⚠️ بدون گروه فعال:\n" + string.Join("، ", missingGroupNames.Take(20));
            if (failures.Count > 0)
                report += "\n\n" + string.Join("\n", failures.Take(20));
            await ReplyAsync(reportChatId, report, ct);
        }
        finally
        {
            PaymentReminderSendLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<PaymentReminderCandidate>> LoadPaymentReminderCandidatesAsync(
        CancellationToken ct,
        bool trackEntities = false)
    {
        var today = DateTime.UtcNow.Date;
        var query = _db.Invoices
            .Include(value => value.Order)!.ThenInclude(value => value!.Customer)!.ThenInclude(value => value!.TelegramGroup)
            .Include(value => value.Order)!.ThenInclude(value => value!.Payments)
            .Where(value =>
                !value.IsDeleted &&
                value.Status == InvoiceStatus.Issued &&
                value.IssuedAt <= DateTime.UtcNow.AddHours(-48) &&
                (value.LastPaymentReminderSentAt == null || value.LastPaymentReminderSentAt < today) &&
                value.Order != null &&
                !value.Order.IsDeleted &&
                value.Order.Status != OrderStatus.Cancelled &&
                value.Order.Customer != null &&
                (value.Order.Customer.LastPaymentReminderSentAt == null ||
                 value.Order.Customer.LastPaymentReminderSentAt < today) &&
                !value.Order.Payments.Any(payment =>
                    !payment.IsDeleted && payment.Status == PaymentStatus.Confirmed));

        if (!trackEntities)
            query = query.AsNoTracking();

        var invoices = await query.ToArrayAsync(ct);
        return invoices.Select(invoice =>
        {
            var customer = invoice.Order!.Customer!;
            var destination = customer.TelegramGroup;
            var hasDestination = destination is not null &&
                                 !destination.IsDeleted &&
                                 destination.IsActive &&
                                 !string.IsNullOrWhiteSpace(destination.ChatId);
            return new PaymentReminderCandidate(
                invoice.Id,
                invoice.InvoiceNumber,
                invoice.TotalAmount,
                invoice.IssuedAt,
                customer.Id,
                customer.FullName,
                customer.Username,
                destination?.ChatId,
                hasDestination);
        }).ToArray();
    }

    private static string BuildCustomerPaymentReminder(
        string messageText,
        IEnumerable<PaymentReminderCandidate> invoices,
        DateTime now)
    {
        var values = invoices.OrderBy(value => value.IssuedAt).ToArray();
        var invoiceLines = values.Select(value =>
            $"• {value.InvoiceNumber} — {value.TotalAmount:N0} تومان — {FormatPaymentReminderAge(value.IssuedAt, now)}");
        return messageText.Trim() + "\n\n" +
               string.Join("\n", invoiceLines);
    }

    private static bool TryGetPaymentReminderDraft(
        long chatId,
        long userId,
        out PaymentReminderDraft draft)
    {
        if (PaymentReminderDrafts.TryGetValue((chatId, userId), out draft!) &&
            draft.UpdatedAt > DateTime.UtcNow.AddMinutes(-20))
            return true;

        PaymentReminderDrafts.TryRemove((chatId, userId), out _);
        draft = null!;
        return false;
    }

    private static bool IsPaymentReminderBucket(string bucket) =>
        bucket is "h48_72" or "d7_8" or "d14_15" or "over30";

    private static bool IsInPaymentReminderBucket(DateTime issuedAt, string bucket, DateTime now)
    {
        var age = now - issuedAt;
        return bucket switch
        {
            "h48_72" => age >= TimeSpan.FromHours(48) && age < TimeSpan.FromHours(72),
            "d7_8" => age >= TimeSpan.FromDays(7) && age < TimeSpan.FromDays(9),
            "d14_15" => age >= TimeSpan.FromDays(14) && age < TimeSpan.FromDays(16),
            "over30" => age >= TimeSpan.FromDays(30),
            _ => false
        };
    }

    private static string PaymentReminderBucketTitle(string bucket) => bucket switch
    {
        "h48_72" => "۴۸ تا ۷۲ ساعت",
        "d7_8" => "۷ تا ۸ روز",
        "d14_15" => "۱۴ تا ۱۵ روز",
        "over30" => "بیش از یک ماه",
        _ => "نامشخص"
    };

    private static string FormatPaymentReminderAge(DateTime issuedAt, DateTime now)
    {
        var age = now - issuedAt;
        return age.TotalDays < 3
            ? $"{Math.Max(0, (int)Math.Floor(age.TotalHours))} ساعت گذشته"
            : $"{Math.Max(0, (int)Math.Floor(age.TotalDays))} روز گذشته";
    }

    private static string FormatPaymentReminderCustomer(PaymentReminderCandidate value) =>
        !string.IsNullOrWhiteSpace(value.CustomerUsername)
            ? $"@{value.CustomerUsername.Trim().TrimStart('@')}"
            : value.CustomerName;
}
