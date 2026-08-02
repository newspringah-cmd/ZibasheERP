using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public interface ITelegramUpdateDeduplicator
{
    bool TryAcquire(long updateId);
    void Release(long updateId);
}

public sealed class TelegramUpdateDeduplicator : ITelegramUpdateDeduplicator
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private const int CleanupThreshold = 10_000;
    private readonly ConcurrentDictionary<long, DateTime> _updates = new();

    public bool TryAcquire(long updateId)
    {
        if (updateId <= 0)
            return true;

        var now = DateTime.UtcNow;
        if (_updates.Count >= CleanupThreshold)
        {
            var cutoff = now - Retention;
            foreach (var item in _updates.Where(item => item.Value < cutoff))
                _updates.TryRemove(item.Key, out _);
        }

        return _updates.TryAdd(updateId, now);
    }

    public void Release(long updateId)
    {
        if (updateId > 0)
            _updates.TryRemove(updateId, out _);
    }
}
