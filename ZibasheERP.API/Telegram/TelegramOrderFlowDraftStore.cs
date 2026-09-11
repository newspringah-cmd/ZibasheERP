using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public enum TelegramOrderShippingStage
{
    AwaitingAddress,
    AwaitingCompany,
    AwaitingCost,
    AwaitingTrackingCode,
    AwaitingConfirmation
}

public sealed class TelegramOrderShippingDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required Guid OrderId { get; init; }
    public Guid? AddressId { get; set; }
    public string ShippingCompany { get; set; } = string.Empty;
    public decimal ShippingCost { get; set; }
    public string TrackingCode { get; set; } = string.Empty;
    public TelegramOrderShippingStage Stage { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TelegramOrderFlowDraftStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramOrderShippingDraft> _drafts = new();
    private readonly ConcurrentDictionary<(long ChatId, long UserId), HashSet<Guid>> _arrivalSelections = new();
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramShippingPreparationDraft> _shippingPreparations = new();
    private readonly ConcurrentDictionary<(long ChatId, long UserId), HashSet<Guid>> _shippingSelections = new();
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramShippingTrackingPhotoDraft> _shippingTrackingPhotos = new();

    public void Set(TelegramOrderShippingDraft draft)
    {
        RemoveExpired();
        draft.UpdatedAt = DateTime.UtcNow;
        _drafts[(draft.ChatId, draft.UserId)] = draft;
    }

    public bool TryGet(long chatId, long userId, out TelegramOrderShippingDraft draft)
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

    public HashSet<Guid> GetArrivalSelection(long chatId, long userId) =>
        _arrivalSelections.GetOrAdd((chatId, userId), _ => []);

    public void ClearArrivalSelection(long chatId, long userId) =>
        _arrivalSelections.TryRemove((chatId, userId), out _);

    public void SetShippingPreparation(TelegramShippingPreparationDraft draft)
    {
        draft.UpdatedAt = DateTime.UtcNow;
        _shippingPreparations[(draft.ChatId, draft.UserId)] = draft;
    }

    public bool TryGetShippingPreparation(long chatId, long userId, out TelegramShippingPreparationDraft draft)
    {
        if (_shippingPreparations.TryGetValue((chatId, userId), out draft!))
        {
            draft.UpdatedAt = DateTime.UtcNow;
            return true;
        }
        draft = _shippingPreparations.Values
            .Where(value => value.ChatId == chatId && value.UpdatedAt >= DateTime.UtcNow - Lifetime)
            .OrderByDescending(value => value.UpdatedAt)
            .FirstOrDefault()!;
        if (draft is null) return false;
        draft.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    public void ClearShippingPreparation(long chatId, long userId) =>
        _shippingPreparations.TryRemove((chatId, userId), out _);

    public HashSet<Guid> GetShippingSelection(long chatId, long userId) =>
        _shippingSelections.GetOrAdd((chatId, userId), _ => []);

    public void SetShippingTrackingPhoto(TelegramShippingTrackingPhotoDraft draft) =>
        _shippingTrackingPhotos[(draft.ChatId, draft.UserId)] = draft;

    public bool TryGetShippingTrackingPhoto(long chatId, long userId, out TelegramShippingTrackingPhotoDraft draft) =>
        _shippingTrackingPhotos.TryGetValue((chatId, userId), out draft!);

    public void ClearShippingTrackingPhoto(long chatId, long userId) =>
        _shippingTrackingPhotos.TryRemove((chatId, userId), out _);

    private void RemoveExpired()
    {
        var threshold = DateTime.UtcNow - Lifetime;
        foreach (var item in _drafts.Where(item => item.Value.UpdatedAt < threshold))
            _drafts.TryRemove(item.Key, out _);
    }
}

public sealed class TelegramShippingTrackingPhotoDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required Guid CustomerId { get; init; }
    public required Guid ShippingRequestId { get; init; }
    public required long SourceMessageId { get; init; }
    public bool HasBatchControls { get; init; }
}

public enum TelegramShippingPreparationStage { AwaitingIdentity, AwaitingAddressChoice, AwaitingNewAddress, Ready }

public sealed class TelegramShippingPreparationDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public Guid CustomerId { get; set; }
    public Guid? AddressId { get; set; }
    public TelegramShippingPreparationStage Stage { get; set; }
    public bool RegistrationOnly { get; set; }
    public bool AllowUnlinkedChat { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
