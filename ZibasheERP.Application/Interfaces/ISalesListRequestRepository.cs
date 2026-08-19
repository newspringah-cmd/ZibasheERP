using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface ISalesListRequestRepository
{
    Task<SalesListRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedAsync(Guid salesListId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedForUserAsync(Guid salesListId, string telegramUserId, CancellationToken cancellationToken = default);
    Task AddAsync(SalesListRequest request, CancellationToken cancellationToken = default);
    Task SelectBottleAsync(Guid requestId, string telegramUserId, Guid bottleId, decimal bottlePrice, CancellationToken cancellationToken = default);
    Task ConfirmCurrentBottleAsync(Guid requestId, string telegramUserId, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid requestId, string telegramUserId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
