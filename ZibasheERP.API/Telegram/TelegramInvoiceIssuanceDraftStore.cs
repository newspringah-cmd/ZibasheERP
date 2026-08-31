using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramInvoiceIssuanceDraftStore
{
    private readonly ConcurrentDictionary<(long ChatId, long UserId), HashSet<Guid>> _drafts = new();

    public HashSet<Guid> GetOrCreate(long chatId, long userId) =>
        _drafts.GetOrAdd((chatId, userId), _ => new HashSet<Guid>());

    public bool TryGet(long chatId, long userId, out HashSet<Guid> selected) =>
        _drafts.TryGetValue((chatId, userId), out selected!);

    public void Remove(long chatId, long userId) => _drafts.TryRemove((chatId, userId), out _);
}

public enum TelegramManualInvoiceStage
{
    AwaitingCustomer,
    AwaitingLine,
    AwaitingLineQuantity,
    AwaitingLineUnitAmount,
    AwaitingLineBottleAmount,
    AwaitingMoreLines,
    AwaitingPhoto,
    AwaitingConfirmation,
    Issuing
}

public sealed class TelegramManualInvoiceDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public TelegramManualInvoiceStage Stage { get; set; } = TelegramManualInvoiceStage.AwaitingCustomer;
    public string CustomerIdentity { get; set; } = string.Empty;
    public string ProductPhotoFileId { get; set; } = string.Empty;
    public string PendingLineDescription { get; set; } = string.Empty;
    public int PendingLineQuantity { get; set; }
    public decimal PendingLineUnitAmount { get; set; }
    public List<ZibasheERP.Application.Interfaces.ManualInvoiceLineInput> Lines { get; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TelegramManualInvoiceDraftStore
{
    private readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramManualInvoiceDraft> _drafts = new();

    public void Set(TelegramManualInvoiceDraft draft)
    {
        draft.UpdatedAt = DateTime.UtcNow;
        _drafts[(draft.ChatId, draft.UserId)] = draft;
    }

    public bool TryGet(long chatId, long userId, out TelegramManualInvoiceDraft draft)
    {
        if (_drafts.TryGetValue((chatId, userId), out var found) && found.UpdatedAt > DateTime.UtcNow.AddMinutes(-20))
        {
            found.UpdatedAt = DateTime.UtcNow;
            draft = found;
            return true;
        }
        _drafts.TryRemove((chatId, userId), out _);
        draft = null!;
        return false;
    }

    public bool TryBeginIssuing(long chatId, long userId, out TelegramManualInvoiceDraft draft)
    {
        if (!_drafts.TryGetValue((chatId, userId), out var found))
        {
            draft = null!;
            return false;
        }
        lock (found)
        {
            if (found.UpdatedAt <= DateTime.UtcNow.AddMinutes(-20) ||
                found.Stage != TelegramManualInvoiceStage.AwaitingConfirmation)
            {
                draft = null!;
                return false;
            }
            found.Stage = TelegramManualInvoiceStage.Issuing;
            found.UpdatedAt = DateTime.UtcNow;
            draft = found;
            return true;
        }
    }

    public void Remove(long chatId, long userId) => _drafts.TryRemove((chatId, userId), out _);
}
