namespace ZibasheERP.Application.Interfaces;

using ZibasheERP.Domain.Entities;

public interface ISalesListRepository
{
    Task<SalesList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SalesList>> GetOpenAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SalesList>> GetForAdminAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<SalesList?> GetLatestByPerfumeIdAsync(
        Guid perfumeId,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveForBatchAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<bool> PublicCodeExistsAsync(int publicCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task AddAsync(SalesList salesList, CancellationToken cancellationToken = default);

    Task UpdateAsync(SalesList salesList, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
