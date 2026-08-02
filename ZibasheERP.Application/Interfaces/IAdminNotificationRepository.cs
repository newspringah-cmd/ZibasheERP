using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IAdminNotificationRepository
{
    Task<IReadOnlyCollection<NotificationOutbox>> GetFailedAsync(int limit, CancellationToken cancellationToken = default);
    Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
