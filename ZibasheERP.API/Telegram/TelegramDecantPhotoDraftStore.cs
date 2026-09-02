using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public enum TelegramDecantPhotoStage
{
    AwaitingPhoto,
    AwaitingSalesList,
    AwaitingConfirmation
}

public sealed class TelegramDecantPhotoDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public TelegramDecantPhotoStage Stage { get; set; } = TelegramDecantPhotoStage.AwaitingPhoto;
    public string PhotoFileId { get; set; } = string.Empty;
    public Guid SalesListId { get; set; }
    public int PublicCode { get; set; }
    public string SalesListName { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TelegramDecantPhotoDraftStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramDecantPhotoDraft> _drafts = new();

    public void Set(TelegramDecantPhotoDraft draft)
    {
        RemoveExpired();
        draft.UpdatedAt = DateTime.UtcNow;
        _drafts[(draft.ChatId, draft.UserId)] = draft;
    }

    public bool TryGet(long chatId, long userId, out TelegramDecantPhotoDraft draft)
    {
        RemoveExpired();
        if (_drafts.TryGetValue((chatId, userId), out var found))
        {
            found.UpdatedAt = DateTime.UtcNow;
            draft = found;
            return true;
        }

        draft = null!;
        return false;
    }

    public void Remove(long chatId, long userId) => _drafts.TryRemove((chatId, userId), out _);

    private void RemoveExpired()
    {
        var threshold = DateTime.UtcNow - Lifetime;
        foreach (var item in _drafts.Where(item => item.Value.UpdatedAt < threshold))
            _drafts.TryRemove(item.Key, out _);
    }
}
