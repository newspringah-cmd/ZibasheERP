using System.Collections.Concurrent;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Telegram;

public enum TelegramAdminSalesListStage
{
    AwaitingPerfumeSearch,
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
    AwaitingBottleOwnerChoice,
    AwaitingBottleOwnerIdentity,
    AwaitingBottleOwnerVolume,
    AwaitingNotes,
    AwaitingPhoto,
    ReviewingExistingPerfume,
    Preview
}

public sealed class TelegramAdminSalesListDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public Guid? PerfumeId { get; set; }
    public bool IsNewPerfume { get; set; }
    public string PerfumeName { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
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
    public string? BottleOwnerIdentity { get; set; }
    public int BottleOwnerVolumeMl { get; set; }
    public string? Notes { get; set; }
    public string? PhotoFileId { get; set; }
    public Guid? SalesListId { get; set; }
    public bool IsReviewingExistingPerfume { get; set; }
    public int ReviewFieldIndex { get; set; }
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

public enum TelegramOwnerPricingKind { BottleRange, PerfumePercentage }
public enum TelegramOwnerPricingStage
{
    AwaitingBottleType,
    AwaitingMinimumVolume,
    AwaitingMaximumVolume,
    AwaitingBottlePrice,
    AwaitingPercentageDirection,
    AwaitingPercentageValue,
    AwaitingConfirmation
}

public sealed class TelegramOwnerPricingDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required TelegramOwnerPricingKind Kind { get; set; }
    public TelegramOwnerPricingStage Stage { get; set; }
    public BottleType? BottleType { get; set; }
    public int MinimumVolumeMl { get; set; }
    public int MaximumVolumeMl { get; set; }
    public decimal Value { get; set; }
    public int PercentageSign { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TelegramOwnerPricingDraftStore
{
    private readonly ConcurrentDictionary<(long, long), TelegramOwnerPricingDraft> _values = new();
    public void Set(TelegramOwnerPricingDraft value) => _values[(value.ChatId, value.UserId)] = value;
    public bool TryGet(long chatId, long userId, out TelegramOwnerPricingDraft value)
    {
        if (_values.TryGetValue((chatId, userId), out var found) &&
            found.CreatedAt > DateTime.UtcNow.AddMinutes(-15))
        {
            value = found;
            return true;
        }
        _values.TryRemove((chatId, userId), out _);
        value = null!;
        return false;
    }
    public void Remove(long chatId, long userId) => _values.TryRemove((chatId, userId), out _);
}

public sealed class TelegramInvoiceStickerDraftStore
{
    private readonly ConcurrentDictionary<(long ChatId, long UserId), DateTime> _values = new();

    public void Start(long chatId, long userId) =>
        _values[(chatId, userId)] = DateTime.UtcNow;

    public bool IsWaiting(long chatId, long userId)
    {
        if (_values.TryGetValue((chatId, userId), out var createdAt) &&
            createdAt > DateTime.UtcNow.AddMinutes(-5))
            return true;
        _values.TryRemove((chatId, userId), out _);
        return false;
    }

    public void Remove(long chatId, long userId) =>
        _values.TryRemove((chatId, userId), out _);
}

public enum TelegramAdminRequestKind
{
    NextBottle,
    CustomRequest,
    GiftRequest,
    EditList,
    CleanupList,
    ManageBottleQueue,
    RemoveCustomerRequests,
    RemoveSingleRequest,
    ChangeRequestVolume,
    OmitRequestIdentityOnLabel,
    SetRequestLabelIdentityText
}
public enum TelegramAdminRequestStage { AwaitingListSearch, AwaitingIdentity, AwaitingGiftRecipient, AwaitingVolume, AwaitingBottleType, AwaitingEditValue, AwaitingEditPhoto, AwaitingQueueVolume, AwaitingQueueIdentity, AwaitingLabelIdentityText, AwaitingConfirmation }

public sealed class TelegramAdminRequestDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required TelegramAdminRequestKind Kind { get; init; }
    public Guid SalesListId { get; set; }
    public int PublicCode { get; set; }
    public string SalesListName { get; set; } = string.Empty;
    public TelegramAdminRequestStage Stage { get; set; } = TelegramAdminRequestStage.AwaitingListSearch;
    public string Identity { get; set; } = string.Empty;
    public bool IsGift { get; set; }
    public string GiftRecipientIdentity { get; set; } = string.Empty;
    public bool IsBottleOwner { get; set; }
    public int VolumeMl { get; set; }
    public BottleType? BottleType { get; set; }
    public string EditField { get; set; } = string.Empty;
    public string EditValue { get; set; } = string.Empty;
    public Guid SelectedRequestId { get; set; }
    public int OriginalVolumeMl { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class TelegramAdminRequestDraftStore
{
    private readonly ConcurrentDictionary<(long, long), TelegramAdminRequestDraft> _values = new();
    public void Set(TelegramAdminRequestDraft value) => _values[(value.ChatId, value.UserId)] = value;
    public bool TryGet(long chatId, long userId, out TelegramAdminRequestDraft value)
    {
        if (_values.TryGetValue((chatId, userId), out var found) &&
            found.CreatedAt > DateTime.UtcNow.AddMinutes(-15))
        {
            value = found;
            return true;
        }
        _values.TryRemove((chatId, userId), out _);
        value = null!;
        return false;
    }
    public void Remove(long chatId, long userId) => _values.TryRemove((chatId, userId), out _);
}
