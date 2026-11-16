using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string TrackingGroupChannel = "CustomerTrackingRequests";
    private sealed record TrackingRequestGroup(Guid CustomerId, Guid PrimaryRequestId, Guid[] RequestIds);

    // Freeze the included requests before sending. Retrying a partially delivered
    // tracking card must neither add later parcels nor forget a completed member.
    private async Task<Guid[]> CustomerTrackingRequestsAsync(TrackingDispatch dispatch, CancellationToken ct)
    {
        if (dispatch.ShippingRequestId is not Guid primary || dispatch.CustomerId is not Guid customer) return [];
        var key = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("tracking-customer-requests:" + dispatch.Id))[..16]);
        await using var own = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        await _db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"tracking-customer-requests:"+customer},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Tracking group busy',1;",ct);
        var receipt = await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n => n.Id == key, ct);
        Guid[] requests;
        if (receipt != null)
        {
            var group = JsonSerializer.Deserialize<TrackingRequestGroup>(receipt.Payload);
            if (receipt.IsDeleted || receipt.Channel != TrackingGroupChannel || receipt.CustomerId != customer ||
                receipt.Recipient != dispatch.Id.ToString("N") || group is null || group.CustomerId != customer ||
                group.PrimaryRequestId != primary || !group.RequestIds.Contains(primary) || group.RequestIds.Length == 0)
                throw new InvalidOperationException("مشخصات درخواست‌های این رهگیری تغییر کرده؛ بررسی لازم است.");
            requests = group.RequestIds.Distinct().Order().ToArray();
            foreach (var request in requests) await ShippingPackageService.ValidateDispatchAsync(_db, request, customer, ct);
        }
        else
        {
            requests = await ShippingPackageService.CustomerDispatchRequestsAsync(_db, primary, customer, ct);
            var prior = await _db.NotificationOutbox.AsNoTracking().Where(n => !n.IsDeleted && n.Channel == TrackingGroupChannel && n.CustomerId == customer).ToArrayAsync(ct);
            foreach (var other in prior)
            {
                var group = JsonSerializer.Deserialize<TrackingRequestGroup>(other.Payload);
                if (group is null || !group.RequestIds.Intersect(requests).Any() || !Guid.TryParse(other.Recipient, out var otherId)) continue;
                if (await _db.TrackingDispatches.AsNoTracking().AnyAsync(d => d.Id == otherId && !d.IsDeleted &&
                    (d.Status == TrackingDispatchStatus.Ready || d.Status == TrackingDispatchStatus.Sending || d.Status == TrackingDispatchStatus.Sent ||
                     d.Deliveries.Any(v => v.AttemptedAt != null || v.SentAt != null)), ct))
                    throw new InvalidOperationException("برای یکی از درخواست‌های این مشتری رهگیری دیگری ثبت شده؛ از ارسال تکراری جلوگیری شد.");
            }
            var now = DateTime.UtcNow;
            _db.NotificationOutbox.Add(new() { Id = key, CustomerId = customer, Channel = TrackingGroupChannel,
                EventType = "TrackingRequestSnapshot", Recipient = dispatch.Id.ToString("N"),
                Payload = JsonSerializer.Serialize(new TrackingRequestGroup(customer, primary, requests)),
                Status = NotificationOutboxStatus.Processed, CreatedAt = now, ProcessedAt = now });
            await _db.SaveChangesAsync(ct);
        }
        if (own != null) await own.CommitAsync(ct);
        return requests;
    }

    private static TrackingDispatch TrackingForRequest(TrackingDispatch original, Guid request, bool destinationSnapshot) => new()
    {
        Id = destinationSnapshot ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("tracking-member-target:" + original.Id + ":" + request))[..16]) : original.Id,
        ImportBatchId = original.ImportBatchId, CustomerId = original.CustomerId, ShippingRequestId = request,
        RecipientName = original.RecipientName, TrackingCode = original.TrackingCode, MatchNotes = original.MatchNotes,
        Status = original.Status, SentAt = original.SentAt, Carrier = original.Carrier
    };

    private async Task<string[]> ResolveTrackingDestinationChatIdsAsync(TrackingDispatch dispatch, CancellationToken ct)
    {
        var requests = await CustomerTrackingRequestsAsync(dispatch, ct);
        var destinations = (await ResolveSingleTrackingDestinationChatIdsAsync(dispatch, ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var request in requests.Where(r => r != dispatch.ShippingRequestId))
        {
            // Ordinary requests of the same customer need one delivery. Explicit
            // shared parcels still notify their companions through the normal rules.
            if (await SharedShippingService.ReadAsync(_db, request, ct) is null) continue;
            var shared = await SharedTrackingDestinationsAsync(TrackingForRequest(dispatch, request, true), ct);
            if (shared != null) destinations.UnionWith(shared);
        }
        return destinations.ToArray();
    }
}
