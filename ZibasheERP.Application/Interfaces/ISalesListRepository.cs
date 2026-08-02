namespace ZibasheERP.Application.Interfaces;

using ZibasheERP.Domain.Entities;

public interface ISalesListRepository
{
    Task<SalesList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(SalesList salesList, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}