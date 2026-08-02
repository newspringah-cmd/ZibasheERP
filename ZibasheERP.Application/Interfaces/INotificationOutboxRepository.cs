using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface INotificationOutboxRepository
{
    Task AddAsync(NotificationOutbox notification, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(
        string channel,
        int limit,
        CancellationToken cancellationToken = default);
    Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
