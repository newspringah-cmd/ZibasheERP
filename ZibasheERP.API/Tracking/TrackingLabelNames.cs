using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Tracking;

public static class TrackingLabelNames
{
    private const string Channel = "ShippingLabelRecipientName";
    private const string EventType = "LabelRecipientExtracted";
    private sealed record LabelSource(Guid AddressId, string Version, string ReceiverName, string[] MobileNumbers);

    public static async Task RecordAsync(AppDbContext db, Address address, string receiverName, CancellationToken ct,
        string? extractedPhones = null, bool sentToPrint = true)
    {
        var name = ShippingAddressDisplay.NormalizeName(receiverName);
        var normalized = TrackingImportService.NormalizeName(name);
        if (!sentToPrint || !ShippingAddressDisplay.IsDisplayName(name) || normalized.Length < 2 ||
            normalized == TrackingImportService.NormalizeName(address.City) ||
            normalized == TrackingImportService.NormalizeName(address.Province) ||
            (normalized != TrackingImportService.NormalizeName(address.ReceiverName) &&
             !TrackingImportService.NormalizeName(address.FullAddress).Contains(normalized, StringComparison.Ordinal)))
            normalized = string.Empty;

        var numbers = AddressLabelService.NormalizePrintPhones(extractedPhones ?? "").Split('،', StringSplitOptions.TrimEntries)
            .Where(number => Regex.IsMatch(number, @"^(?:09|\+989|00989)[0-9]{9}$")).ToArray();
        if (normalized.Length == 0 && numbers.Length == 0)
            return;

        var version = Version(address);
        var payload = JsonSerializer.Serialize(new LabelSource(address.Id, version, name, numbers));
        var key = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Channel, address.CustomerId, AddressId = address.Id, version, normalized, numbers }))[..16]);
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox (Id,CustomerId,Channel,EventType,Recipient,Payload,Status,Attempts,CreatedAt,ProcessedAt,IsDeleted)
            SELECT {key},{address.CustomerId},{Channel},{EventType},{normalized},{payload},{(int)NotificationOutboxStatus.Processed},0,{now},{now},CAST(0 AS bit)
            WHERE NOT EXISTS (SELECT 1 FROM NotificationOutbox WITH (UPDLOCK,HOLDLOCK) WHERE Id={key});
            """, ct);
    }

    internal static async Task<Dictionary<Guid, Guid>> FindAddressesAsync(AppDbContext db, string name, DateTime asOf, CancellationToken ct)
    {
        var rows = await db.NotificationOutbox.AsNoTracking().Where(r => !r.IsDeleted && r.Channel == Channel &&
            r.EventType == EventType && r.Status == NotificationOutboxStatus.Processed && r.Recipient == name &&
            r.CreatedAt <= asOf).Select(r => new { r.CustomerId, r.Payload }).ToArrayAsync(ct);
        var sources = new HashSet<(Guid CustomerId, Guid AddressId, string Version)>();
        foreach (var row in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<LabelSource>(row.Payload) is { } source)
                    sources.Add((row.CustomerId, source.AddressId, source.Version));
            }
            catch (JsonException) { /* Incomplete receipt metadata cannot identify a recipient. */ }
        }
        if (sources.Count == 0) return [];

        var addressIds = sources.Select(row => row.AddressId).Distinct().ToArray();
        var addresses = await db.Addresses.AsNoTracking().Where(a => !a.IsDeleted && addressIds.Contains(a.Id)).ToArrayAsync(ct);
        return addresses.Where(a => sources.Contains((a.CustomerId, a.Id, Version(a))))
            .ToDictionary(a => a.Id, a => a.CustomerId);
    }

    private static string Version(Address a) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Created = a.CreatedAt.Ticks, Updated = a.UpdatedAt?.Ticks, a.ReceiverName, a.FullAddress,
            a.Mobile, a.City, a.Province, a.PostalCode, a.Description })));
}
