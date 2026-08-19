using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public enum TelegramAdminSalesListStage
{
    AwaitingEnglishName,
    AwaitingProductPageUrl,
    AwaitingBrand,
    AwaitingGender,
    AwaitingReleaseYear,
    AwaitingPersianName,
    AwaitingTopNotes,
    AwaitingMiddleNotes,
    AwaitingBaseNotes,
    AwaitingAccords,
    AwaitingPrice,
    AwaitingVolume,
    AwaitingMinimumVolume,
    AwaitingNotes,
    AwaitingPhoto,
    Preview
}

public sealed class TelegramAdminSalesListDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required Guid BatchId { get; init; }
    public required string BatchNumber { get; init; }
    public required string PerfumeName { get; init; }
    public required string Brand { get; init; }
    public required decimal BatchRemainingVolumeMl { get; init; }
    public TelegramAdminSalesListStage Stage { get; set; } = TelegramAdminSalesListStage.AwaitingEnglishName;
    public string EnglishName { get; set; } = string.Empty;
    public string ProductPageUrl { get; set; } = string.Empty;
    public string DisplayBrand { get; set; } = string.Empty;
    public int Gender { get; set; } = 3;
    public int ReleaseYear { get; set; }
    public string PersianName { get; set; } = string.Empty;
    public string TopNotes { get; set; } = string.Empty;
    public string MiddleNotes { get; set; } = string.Empty;
    public string BaseNotes { get; set; } = string.Empty;
    public string Accords { get; set; } = string.Empty;
    public decimal PricePerMl { get; set; }
    public int TotalVolume { get; set; }
    public int MinimumRequestVolumeMl { get; set; }
    public string? Notes { get; set; }
    public string? PhotoFileId { get; set; }
    public Guid? SalesListId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TelegramAdminSalesListDraftStore
{
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramAdminSalesListDraft> _drafts = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    public void Set(TelegramAdminSalesListDraft draft)
    {
        RemoveExpired();
        draft.UpdatedAt = DateTime.UtcNow;
        _drafts[(draft.ChatId, draft.UserId)] = draft;
    }

    public bool TryGet(long chatId, long userId, out TelegramAdminSalesListDraft draft)
    {
        RemoveExpired();
        if (_drafts.TryGetValue((chatId, userId), out var value))
        {
            value.UpdatedAt = DateTime.UtcNow;
            draft = value;
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
