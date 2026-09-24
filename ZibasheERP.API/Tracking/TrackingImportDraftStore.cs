using System.Collections.Concurrent;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Tracking;

public sealed record TrackingImportDraft(
    long ChatId,
    long UserId,
    TrackingCarrier Carrier,
    DateTime ExpiresAt);

public sealed class TrackingImportDraftStore
{
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TrackingImportDraft> _drafts = new();

    public void Set(long chatId, long userId, TrackingCarrier carrier) =>
        _drafts[(chatId, userId)] = new TrackingImportDraft(chatId, userId, carrier, DateTime.UtcNow.AddMinutes(20));

    public bool TryGet(long chatId, long userId, out TrackingImportDraft draft)
    {
        if (_drafts.TryGetValue((chatId, userId), out draft!) && draft.ExpiresAt > DateTime.UtcNow)
            return true;
        _drafts.TryRemove((chatId, userId), out _);
        draft = null!;
        return false;
    }

    public void Remove(long chatId, long userId) => _drafts.TryRemove((chatId, userId), out _);
}
