using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface ISalesListRepository
{
    Task<SalesList?> GetByIdAsync(Guid id);

    Task UpdateAsync(SalesList salesList);
}