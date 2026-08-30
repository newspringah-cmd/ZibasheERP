using System.Collections.Concurrent;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramInvoiceInventoryDraft
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required Guid OrderItemId { get; init; }
    public decimal? NewTotalAmount { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
}

public sealed class TelegramInvoiceInventoryDraftStore
{
    private readonly ConcurrentDictionary<(long, long), TelegramInvoiceInventoryDraft> _items = new();
    public void Set(TelegramInvoiceInventoryDraft draft) => _items[(draft.ChatId, draft.UserId)] = draft;
    public bool TryGet(long chatId, long userId, out TelegramInvoiceInventoryDraft draft)
    {
        if (_items.TryGetValue((chatId, userId), out draft!) && draft.ExpiresAt > DateTime.UtcNow) return true;
        _items.TryRemove((chatId, userId), out _); draft = null!; return false;
    }
    public void Remove(long chatId, long userId) => _items.TryRemove((chatId, userId), out _);
}
