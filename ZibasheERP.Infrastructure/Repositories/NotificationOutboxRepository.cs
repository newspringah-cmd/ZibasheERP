using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class NotificationOutboxRepository : INotificationOutboxRepository, IAdminNotificationRepository
{
    private readonly AppDbContext _dbContext;

    public NotificationOutboxRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(NotificationOutbox notification, CancellationToken cancellationToken = default)
    {
        await _dbContext.NotificationOutbox.AddAsync(notification, cancellationToken);
    }

    public async Task<IReadOnlyCollection<NotificationOutbox>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.NotificationOutbox
            .AsNoTracking()
            .Where(notification =>
                notification.Status == NotificationOutboxStatus.Pending &&
                !notification.IsDeleted)
            .OrderBy(notification => notification.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArrayAsync(cancellationToken);
    }

    public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _dbContext.NotificationOutbox.FirstOrDefaultAsync(
            notification => notification.Id == id && !notification.IsDeleted,
            cancellationToken);
    }

    public async Task<IReadOnlyCollection<NotificationOutbox>> GetFailedAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await _dbContext.NotificationOutbox
            .AsNoTracking()
            .Where(notification =>
                notification.Status == NotificationOutboxStatus.Failed &&
                !notification.IsDeleted)
            .OrderByDescending(notification => notification.UpdatedAt ?? notification.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArrayAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
