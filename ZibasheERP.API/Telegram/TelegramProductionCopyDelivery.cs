using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed record ProductionCopyDeliveryReceipt(Guid SalesListId, long? HeaderMessageId = null,
    IReadOnlyList<long>? BodyMessageIds = null, bool Complete = false, string? Error = null);

public interface IProductionCopyDeliveryStore
{
    Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct);
    Task<ProductionCopyDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct);
    Task SaveAsync(Guid key, ProductionCopyDeliveryReceipt receipt, CancellationToken ct);
}

// A dedicated outbox channel is a durable idempotency receipt. One completed
// sales-list cycle can produce at most one label copy per destination group.
public sealed class ProductionCopyDeliveryStore(AppDbContext db) : IProductionCopyDeliveryStore
{
    public const string Channel = "ProductionCopyDelivery";

    public async Task<bool> TryBeginAsync(Guid key, Guid listId, string chatId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new ProductionCopyDeliveryReceipt(listId));
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [NotificationOutbox]
                ([Id],[CustomerId],[Channel],[EventType],[Recipient],[Payload],[Status],[Attempts],[CreatedAt],[IsDeleted])
            SELECT {key},{Guid.Empty},{Channel},{"ProductionCopyDelivery"},{chatId},{payload},
                {NotificationOutboxStatus.Processing},{1},{now},{false}
            WHERE NOT EXISTS (
                SELECT 1 FROM [NotificationOutbox] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={key}
            );
            """, ct) == 1;
    }

    public async Task<ProductionCopyDeliveryReceipt?> GetAsync(Guid key, CancellationToken ct)
    {
        var payload = await db.NotificationOutbox.AsNoTracking()
            .Where(row => row.Id == key && row.Channel == Channel)
            .Select(row => row.Payload).SingleOrDefaultAsync(ct);
        return payload is null ? null : JsonSerializer.Deserialize<ProductionCopyDeliveryReceipt>(payload);
    }

    public async Task SaveAsync(Guid key, ProductionCopyDeliveryReceipt receipt, CancellationToken ct)
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
        if (affected != 1) throw new InvalidOperationException("Production-copy delivery receipt is missing.");
    }
}

public sealed class TelegramProductionCopyDelivery(
    IProductionCopyDeliveryStore store,
    ITelegramMessageSender sender)
{
    public static Guid CreateKey(Guid listId, string chatId) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"ProductionCopyDelivery:{listId:N}:{chatId.Trim()}"))[..16]);

    public async Task<TelegramSendResult> SendAsync(Guid listId, string chatId, string? photo,
        string header, IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons,
        string labelPrintMessage, CancellationToken ct)
    {
        chatId = chatId.Trim();
        var key = CreateKey(listId, chatId);
        if (!await store.TryBeginAsync(key, listId, chatId, ct))
        {
            var previous = await store.GetAsync(key, ct);
            return previous?.Complete == true
                ? new(true, MessageId: previous.HeaderMessageId)
                : new(false, "ارسال قبلی در حال انجام یا نیازمند بررسی است؛ برای جلوگیری از چاپ تکراری، ارسال دوباره انجام نشد.",
                    previous?.HeaderMessageId);
        }

        var receipt = new ProductionCopyDeliveryReceipt(listId);
        try
        {
            var headerResult = string.IsNullOrWhiteSpace(photo)
                ? await sender.SendInlineKeyboardAsync(chatId, header + "\n⚠️ عکس عطر ثبت نشده است.", buttons, ct)
                : await sender.SendPhotoWithKeyboardAsync(chatId, photo, header, buttons, ct);
            if (!headerResult.IsSuccessful || !headerResult.MessageId.HasValue)
                throw new InvalidOperationException(headerResult.Error ?? "شناسه پیام چاپ لیبل دریافت نشد.");

            receipt = receipt with { HeaderMessageId = headerResult.MessageId };
            await store.SaveAsync(key, receipt, CancellationToken.None);
            var bodyMessageIds = new List<long>();
            foreach (var part in SplitMessage(labelPrintMessage))
            {
                var bodyResult = await sender.SendAsync(chatId, part, ct);
                if (!bodyResult.IsSuccessful || !bodyResult.MessageId.HasValue)
                    throw new InvalidOperationException(bodyResult.Error ?? "ارسال متن چاپ لیبل تأیید نشد.");
                bodyMessageIds.Add(bodyResult.MessageId.Value);
                receipt = receipt with { BodyMessageIds = bodyMessageIds.ToArray() };
                await store.SaveAsync(key, receipt, CancellationToken.None);
            }

            await store.SaveAsync(key, receipt with { Complete = true }, CancellationToken.None);
            return new(true, MessageId: receipt.HeaderMessageId);
        }
        catch (Exception error)
        {
            // Keep the claim if Telegram may have accepted a message. An operator
            // must review the saved message ids before any deliberate resend.
            var reason = error is OperationCanceledException
                ? "ارسال لغو شد؛ نتیجه باید بررسی شود."
                : error is InvalidOperationException ? error.Message : "نتیجه ارسال نامشخص است؛ نیاز به بررسی دارد.";
            await store.SaveAsync(key, receipt with { Error = reason[..Math.Min(reason.Length, 1000)] }, CancellationToken.None);
            return new(false, reason, receipt.HeaderMessageId);
        }
    }

    private static IReadOnlyCollection<string> SplitMessage(string message)
    {
        const int maxLength = 3900;
        if (message.Length <= maxLength) return new[] { message };
        var parts = new List<string>();
        var remaining = message;
        while (remaining.Length > maxLength)
        {
            var splitAt = remaining.LastIndexOf('\n', maxLength);
            if (splitAt < maxLength / 2) splitAt = maxLength;
            parts.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }
        if (remaining.Length > 0) parts.Add(remaining);
        return parts;
    }
}
