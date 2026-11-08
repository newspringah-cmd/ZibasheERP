using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public enum ShippingPackApprovalState { Sending, AwaitingCustomer, Confirmed, RevisionRequested, NeedsReview, Dispatched, Superseded }
public sealed record ShippingPackPhotoDelivery(string ChatId, int Index, DateTime? AttemptedAt = null,
    DateTime? SentAt = null, long? MessageId = null, string? Error = null);
public sealed record ShippingPackApprovalData
{
    public Guid CustomerId { get; init; }
    public Guid? PhotoDraftId { get; init; }
    public Guid? RequestId { get; init; }
    public bool LegacyAddressOnly { get; init; }
    public Guid? AddressId { get; init; }
    public long? AddressMessageId { get; init; }
    public Guid[] ItemIds { get; init; } = [];
    public string[] PhotoFileIds { get; init; } = [];
    public string ShippingChatId { get; init; } = "";
    public long OperatorUserId { get; init; }
    public ShippingPackApprovalState State { get; init; }
    public ShippingPackPhotoDelivery[] Deliveries { get; init; } = [];
    public DateTime? SentAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
    public long? ConfirmedBy { get; init; }
    public DateTime? DispatchedAt { get; init; }
    public DateTime? OperatorNoticeAttemptedAt { get; init; }
    public Guid? ConfirmationResponseId { get; init; }
    public Guid? RevisionDraftId { get; init; }
    public Guid? RevisionResponseId { get; init; }
    public string? RevisionText { get; init; }
    public DateTime? RevisionSubmittedAt { get; init; }
}
public static class ShippingPackApproval
{
    public const string ReceiptChannel = "ShippingPackApprovalReceipt";
    public const string SendChannel = "ShippingPackApprovalSend";
    public const string Caption = "سلام عزیزم اقلام ارسالی پست شما لطفا چک کنید و تایید بدین که ارسال بشه❤️";

    public static async Task MarkDispatchedAsync(AppDbContext db, Guid customerId, Guid? requestId,
        IReadOnlyCollection<Guid> shippedItemIds, CancellationToken ct = default)
    {
        await using var own = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
        var rows = await db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE IsDeleted=0 AND Channel={ReceiptChannel} AND CustomerId={customerId}").AsNoTracking()
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).ToArrayAsync(ct);
        foreach (var row in rows)
        {
            var receipt = JsonSerializer.Deserialize<ShippingPackApprovalData>(row.Payload)!;
            if(receipt.LegacyAddressOnly)continue;
            if (receipt.State == ShippingPackApprovalState.Superseded ||
                !(requestId.HasValue && receipt.RequestId == requestId || receipt.RequestId == null && receipt.ItemIds.Intersect(shippedItemIds).Any())) continue;
            // Close the actual parcel's photo flow without inventing customer
            // approval. Changed photo contents cannot prevent tracking delivery.
            if (!requestId.HasValue && !receipt.ItemIds.All(shippedItemIds.Contains)) continue;
            var updated = receipt with { State = ShippingPackApprovalState.Dispatched, DispatchedAt = receipt.DispatchedAt ?? DateTime.UtcNow };
            await db.NotificationOutbox.Where(n => n.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.Payload, JsonSerializer.Serialize(updated, (JsonSerializerOptions?)null))
                .SetProperty(n => n.UpdatedAt, DateTime.UtcNow), ct);
            break;
        }
        if (own != null) await own.CommitAsync(ct);
    }
}
