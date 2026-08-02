using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public interface ITelegramUpdateDeduplicator
{
    Task<bool> TryAcquireAsync(long updateId, CancellationToken cancellationToken = default);
    Task ReleaseAsync(long updateId, CancellationToken cancellationToken = default);
}

public sealed class TelegramUpdateDeduplicator : ITelegramUpdateDeduplicator
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private const int CleanupInterval = 1_000;
    private readonly IServiceScopeFactory _scopeFactory;
    private long _acquisitionCount;

    public TelegramUpdateDeduplicator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> TryAcquireAsync(
        long updateId,
        CancellationToken cancellationToken = default)
    {
        if (updateId <= 0)
            return true;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.TelegramProcessedUpdates.Add(new TelegramProcessedUpdate
        {
            UpdateId = updateId,
            ReceivedAt = DateTime.UtcNow
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return false;
        }

        if (Interlocked.Increment(ref _acquisitionCount) % CleanupInterval == 0)
        {
            var cutoff = DateTime.UtcNow - Retention;
            await dbContext.TelegramProcessedUpdates
                .Where(update => update.ReceivedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }

        return true;
    }

    public async Task ReleaseAsync(
        long updateId,
        CancellationToken cancellationToken = default)
    {
        if (updateId <= 0)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.TelegramProcessedUpdates
            .Where(update => update.UpdateId == updateId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
