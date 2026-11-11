using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Telegram;

public static class ManualShippingAddresses
{
    public const string Channel = "ManualShippingAddressSent";
    public const string ControlsChannel = "ManualShippingAddressControls";
    public sealed record Root(Guid CustomerId, Guid? RequestId, Guid[] ItemIds, string ChatId, string Kind,
        long? MessageId, bool Installed, Guid? AddressId);
    public sealed record Source(Guid ReferenceId, Root Root, DateTime CreatedAt);
    public sealed record Completion(Guid ReferenceId, Guid CustomerId, Guid AddressId, string ChatId,
        long MessageId, DateTime SentAt, long? Actor, string Reason);
    public static Guid Key(Guid reference) => new(SHA256.HashData(Encoding.UTF8.GetBytes("manual-address-sent:" + reference.ToString("N")))[..16]);

    public static async Task<Source[]> SourcesAsync(AppDbContext db, TelegramOptions options, CancellationToken ct)
    {
        var chat = options.ShippingChatId.Trim();
        var rows = await db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.Channel == "ShippingReferenceContext" &&
            n.EventType == "AddressReferences" && n.Recipient == chat).ToArrayAsync(ct);
        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToArray();
        var customers = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted && customerIds.Contains(c.Id) &&
            c.Notes != null && c.Notes.StartsWith("manual-address-registration:")).ToDictionaryAsync(c => c.Id, ct);
        var result = new List<Source>();
        foreach (var row in rows)
        {
            Root? root;
            try { root = JsonSerializer.Deserialize<Root>(row.Payload); } catch (JsonException) { continue; }
            if (root is not { Kind: "root", Installed: true, MessageId: > 0, RequestId: null, ItemIds.Length: 0 } ||
                root.CustomerId != row.CustomerId || root.ChatId != chat || root.AddressId is null ||
                !customers.TryGetValue(row.CustomerId, out var customer)) continue;
            try { if (ManualShippingRegistration.TrackingDestination(customer, options.OwnerUserId) is null) continue; }
            catch (InvalidOperationException) { continue; }
            result.Add(new(row.Id, root, row.CreatedAt));
        }
        var addressIds = result.Select(r => r.Root.AddressId!.Value).Distinct().ToArray();
        var addresses = await db.Addresses.AsNoTracking().Where(a => !a.IsDeleted && addressIds.Contains(a.Id)).Select(a => new { a.Id, a.CustomerId }).ToArrayAsync(ct);
        return result.Where(r => addresses.Any(a => a.Id == r.Root.AddressId && a.CustomerId == r.Root.CustomerId))
            .OrderByDescending(r => r.CreatedAt).DistinctBy(r => r.Root.MessageId).ToArray();
    }

    public static async Task<bool> IsSentAsync(AppDbContext db, Source source, CancellationToken ct)
    {
        var row = await db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n => n.Id == Key(source.ReferenceId) && !n.IsDeleted && n.Channel == Channel, ct);
        if (row is null) return false;
        var d = JsonSerializer.Deserialize<Completion>(row.Payload);
        return row.Status == NotificationOutboxStatus.Processed && row.CustomerId == source.Root.CustomerId && row.Recipient == source.Root.ChatId &&
            d is not null && d.ReferenceId == source.ReferenceId && d.CustomerId == source.Root.CustomerId && d.ChatId == source.Root.ChatId &&
            d.AddressId == source.Root.AddressId && d.MessageId == source.Root.MessageId;
    }

    public static async Task CompleteAsync(AppDbContext db, Source source, long? actor, string reason, CancellationToken ct)
    {
        if (await IsSentAsync(db, source, ct)) return;
        var id = Key(source.ReferenceId); var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new Completion(source.ReferenceId, source.Root.CustomerId, source.Root.AddressId!.Value,
            source.Root.ChatId, source.Root.MessageId!.Value, now, actor, reason));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox (Id,CustomerId,Channel,EventType,Recipient,Payload,Status,Attempts,CreatedAt,ProcessedAt,IsDeleted)
            SELECT {id},{source.Root.CustomerId},{Channel},N'ManualParcelCompleted',{source.Root.ChatId},{payload},2,0,{now},{now},CAST(0 AS bit)
            WHERE NOT EXISTS(SELECT 1 FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id});
            """, ct);
        if (!await IsSentAsync(db, source, ct)) throw new InvalidOperationException("ثبت وضعیت ارسال آدرس نیازمند بررسی است.");
    }

    public static async Task<bool> HasTrackingProofAsync(AppDbContext db, Source source, Source[] sources, TelegramOptions options, CancellationToken ct)
    {
        var notices = await db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.Channel == ShippingTrackingNotices.Channel &&
            n.CustomerId == source.Root.CustomerId && n.Recipient == source.Root.ChatId).Select(n => n.Payload).ToArrayAsync(ct);
        foreach (var payload in notices)
        {
            ShippingTrackingNotices.Data? d; try { d = JsonSerializer.Deserialize<ShippingTrackingNotices.Data>(payload); } catch (JsonException) { continue; }
            if (d is not null && d.CustomerId == source.Root.CustomerId && d.ChatId == source.Root.ChatId && d.SourceMessageId == source.Root.MessageId) return true;
        }
        // Manual registrations have dedicated customer identities. Never use a manager recipient or a similar name as proof.
        if (sources.Where(s => s.Root.CustomerId == source.Root.CustomerId).Select(s => s.Root.AddressId).Distinct().Count() != 1) return false;
        return await db.TrackingDispatches.AsNoTracking().AnyAsync(d => !d.IsDeleted && d.CustomerId == source.Root.CustomerId &&
            d.ShippingRequestId == null && d.Status == TrackingDispatchStatus.Sent && d.SentAt >= source.CreatedAt &&
            d.Deliveries.Any(v => !v.IsDeleted && v.TelegramChatId == options.OwnerUserId.Trim() && v.SentAt >= source.CreatedAt), ct);
    }
}
