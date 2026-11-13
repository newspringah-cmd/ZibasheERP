using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class AutomaticDirhamRateService(AppDbContext db, TimeProvider? clock = null)
{
    public const string ReceiptChannel = "AlanchandDirhamRate";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(2);
    private DateTime Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public static bool IsDue(DateTime? lastSuccessUtc, DateTime nowUtc) =>
        !lastSuccessUtc.HasValue || nowUtc - lastSuccessUtc.Value >= Interval;

    public Task<DateTime?> LastSuccessAsync(CancellationToken ct) => db.NotificationOutbox.AsNoTracking()
        .Where(n => !n.IsDeleted && n.Channel == ReceiptChannel && n.Status == NotificationOutboxStatus.Processed)
        .OrderByDescending(n => n.ProcessedAt).Select(n => n.ProcessedAt).FirstOrDefaultAsync(ct);

    public async Task<bool> RegisterAsync(AlanchandDirhamQuote quote, CancellationToken ct)
    {
        var now = Now;
        if (quote.TomanPerDirham <= 0 || quote.TomanPerDirham > 1_000_000_000m ||
            decimal.Round(quote.TomanPerDirham, 4) != quote.TomanPerDirham ||
            quote.SourceUnit is not ("Toman" or "IRR") || quote.DocumentSha256.Length != 64 ||
            quote.FetchedAtUtc > now || now - quote.FetchedAtUtc > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Invalid or expired dirham observation.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"dirham-purchase-ledger"},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Dirham ledger busy',1;", ct);
        // The same lock is used by manual entry. Replicas and restarts cannot insert twice.
        if (!IsDue(await LastSuccessAsync(ct), now)) { await tx.CommitAsync(ct); return false; }
        var latest = await db.DirhamExchangeRates.AsNoTracking().OrderByDescending(r => r.EffectiveAtUtc)
            .Select(r => (DateTime?)r.EffectiveAtUtc).FirstOrDefaultAsync(ct);
        if (latest >= now) throw new InvalidOperationException("Exchange-rate ledger has a future timestamp.");
        var id = Guid.NewGuid();
        db.DirhamExchangeRates.Add(new() { Id = id, TomanPerDirham = quote.TomanPerDirham,
            RecordedByUserId = 0, EffectiveAtUtc = now, CreatedAt = now });
        db.NotificationOutbox.Add(new() { Id = id, CustomerId = Guid.Empty, Channel = ReceiptChannel,
            EventType = "AutomaticDirhamRateRecorded", Recipient = "system", CreatedAt = now,
            Status = NotificationOutboxStatus.Processed, ProcessedAt = now,
            Payload = JsonSerializer.Serialize(new { Source = AlanchandDirhamRateSource.SourceUrl,
                quote.TomanPerDirham, quote.SourceUnit, quote.PriceColumn, quote.SourcePriceText,
                quote.FetchedAtUtc, quote.DocumentSha256, EffectiveAtUtc = now }) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
