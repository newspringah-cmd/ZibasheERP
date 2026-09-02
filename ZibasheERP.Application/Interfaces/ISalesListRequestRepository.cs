using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface ISalesListRequestRepository
{
    Task<SalesListRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedAsync(Guid salesListId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SalesListRequest>> GetForLabelAdministrationAsync(Guid salesListId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<SalesListRequest>> GetConfirmedForUserAsync(Guid salesListId, string telegramUserId, CancellationToken cancellationToken = default);
    Task AddAsync(SalesListRequest request, CancellationToken cancellationToken = default);
    Task SetGiftRecipientAsync(Guid requestId, string telegramUserId, string recipientIdentity, CancellationToken cancellationToken = default);
    Task<bool> IsGiftRecipientBottleOwnerAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task SelectBottleAsync(Guid requestId, string telegramUserId, Guid bottleId, decimal bottlePrice, CancellationToken cancellationToken = default);
    Task ConfirmCurrentBottleAsync(Guid requestId, string telegramUserId, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid requestId, string telegramUserId, CancellationToken cancellationToken = default);
    Task RemoveConfirmedAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task PromoteNextBottleOwnerAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task UpdateConfirmedVolumeAsync(Guid requestId, int volumeMl, CancellationToken cancellationToken = default);
    Task UpdateBottleOwnerIdentityAsync(Guid requestId, string identity, CancellationToken cancellationToken = default);
    Task SetOmitIdentityOnLabelAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task SetLabelIdentityTextAsync(Guid requestId, string labelIdentityText, CancellationToken cancellationToken = default);
    Task<int> CountActiveCustomerRequestsAsync(string identity, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Guid>> RemoveAllActiveCustomerRequestsAsync(string identity, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
