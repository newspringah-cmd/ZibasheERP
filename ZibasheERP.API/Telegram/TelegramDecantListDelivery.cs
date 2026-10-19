using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed record DecantListDeliveryReceipt(Guid SalesListId, long? MessageId = null,
    long? ContinuationMessageId = null, bool Complete = false, string? Error = null);

public interface IDecantListDeliveryStore
{
    Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct);
    Task<DecantListDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct);
    Task SaveAsync(Guid key, DecantListDeliveryReceipt receipt, CancellationToken ct);
}

// A dedicated outbox channel stores permanent delivery receipts; generic workers
// do not consume it. The primary key and range lock serialize competing senders.
public sealed class DecantListDeliveryStore(AppDbContext db) : IDecantListDeliveryStore
{
    public const string Channel = "DecantListDelivery";

    public async Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new DecantListDeliveryReceipt(listId));
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [NotificationOutbox]
                ([Id],[CustomerId],[Channel],[EventType],[Recipient],[Payload],[Status],[Attempts],[CreatedAt],[IsDeleted])
            SELECT {key},{Guid.Empty},{Channel},{"DecantListDelivery"},{chatId},{payload},{NotificationOutboxStatus.Processing},{1},{now},{false}
            WHERE NOT EXISTS (
                SELECT 1 FROM [NotificationOutbox] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={key}
            );
            """, ct) == 1;
    }

    public async Task<DecantListDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct)
    {
        var payload = await db.NotificationOutbox.AsNoTracking().Where(row => row.Id == key && row.Channel == Channel)
            .Select(row => row.Payload).SingleOrDefaultAsync(ct);
        return payload is null ? null : JsonSerializer.Deserialize<DecantListDeliveryReceipt>(payload);
    }

    public async Task SaveAsync(Guid key, DecantListDeliveryReceipt receipt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(receipt);
        var status = receipt.Complete ? NotificationOutboxStatus.Processed :
            receipt.Error is null ? NotificationOutboxStatus.Processing : NotificationOutboxStatus.Failed;
        var now = DateTime.UtcNow;
        var affected = await db.NotificationOutbox.Where(row => row.Id == key && row.Channel == Channel)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Payload, payload)
                .SetProperty(row => row.Status, status).SetProperty(row => row.LastError, receipt.Error)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.ProcessedAt, receipt.Complete ? (DateTime?)now : null), ct);
        if (affected != 1) throw new InvalidOperationException("Decant delivery receipt is missing.");
    }
}

public sealed class TelegramDecantListDelivery(IDecantListDeliveryStore store, ITelegramMessageSender sender)
{
    public static Guid CreateKey(Guid listId, string chatId) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"DecantListDelivery:{listId:N}:{chatId.Trim()}"))[..16]);

    public async Task<TelegramSendResult> SendAsync(Guid listId, string chatId, string? photo, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons, string? continuation, CancellationToken ct)
    {
        chatId = chatId.Trim();
        var key = CreateKey(listId, chatId);
        if (!await store.TryBeginAsync(key, listId, chatId, ct))
        {
            var previous = await store.GetAsync(key, ct);
            return previous?.Complete == true
                ? new(true, MessageId: previous.MessageId)
                : new(false, "ارسال قبلی در حال انجام است یا نتیجه‌اش نیاز به بررسی دارد؛ برای جلوگیری از پیام تکراری دوباره ارسال نشد.", previous?.MessageId);
        }

        var receipt = new DecantListDeliveryReceipt(listId);
        try
        {
            var main = await sender.SendDecantListPartAsync(chatId, photo, caption, buttons, ct);
            if (!main.IsSuccessful || !main.MessageId.HasValue)
                throw new InvalidOperationException(main.Error ?? "شناسه پیام دکانت دریافت نشد.");
            receipt = receipt with { MessageId = main.MessageId };
            // Persist the acknowledged main message before sending its continuation.
            await store.SaveAsync(key, receipt, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(continuation))
            {
                var extra = await sender.SendDecantListPartAsync(chatId, null, continuation,
                    Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
                if (!extra.IsSuccessful || !extra.MessageId.HasValue)
                    throw new InvalidOperationException(extra.Error ?? "ارسال ادامه لیست تأیید نشد.");
                receipt = receipt with { ContinuationMessageId = extra.MessageId };
            }
            await store.SaveAsync(key, receipt with { Complete = true }, CancellationToken.None);
            return new(true, MessageId: receipt.MessageId);
        }
        catch (Exception error)
        {
            // Do not release a claim on timeouts or a crash: Telegram may have
            // accepted the message. A deliberate resend must review this receipt.
            var reason = error is OperationCanceledException ? "ارسال لغو شد؛ نتیجه باید بررسی شود." :
                error is InvalidOperationException ? error.Message : "نتیجه ارسال نامشخص است؛ نیاز به بررسی دارد.";
            await store.SaveAsync(key, receipt with { Error = reason[..Math.Min(reason.Length, 1000)] }, CancellationToken.None);
            return new(false, reason, receipt.MessageId);
        }
    }
}
