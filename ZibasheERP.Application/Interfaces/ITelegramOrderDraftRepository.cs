using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface ITelegramOrderDraftRepository
{
    Task<TelegramOrderDraft?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(TelegramOrderDraft draft, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
