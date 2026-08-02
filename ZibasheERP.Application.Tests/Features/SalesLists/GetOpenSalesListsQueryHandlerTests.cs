using ZibasheERP.Application.Features.SalesLists.GetOpenSalesLists;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.SalesLists;

public sealed class GetOpenSalesListsQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsPerfumeAndRemainingVolume()
    {
        var perfume = new Perfume
        {
            Name = "عطر تست",
            EnglishName = "Test Perfume",
            Brand = "Test Brand"
        };
        var salesList = new SalesList
        {
            Id = Guid.NewGuid(),
            Batch = new Batch { Perfume = perfume },
            PricePerMl = 250_000,
            TotalVolume = 100,
            ReservedVolume = 35,
            Status = SalesListStatus.Open
        };
        var handler = new GetOpenSalesListsQueryHandler(
            new SalesListRepositoryStub(salesList));

        var result = await handler.Handle(
            new GetOpenSalesListsQuery(),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal(65, result.Single().RemainingVolumeMl);
        Assert.Equal("Test Brand", result.Single().Brand);
    }

    private sealed class SalesListRepositoryStub(params SalesList[] lists) : ISalesListRepository
    {
        public Task<IReadOnlyCollection<SalesList>> GetOpenAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<SalesList>>(lists.Take(limit).ToArray());
        public Task<SalesList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SalesList?>(lists.FirstOrDefault(value => value.Id == id));
        public Task UpdateAsync(SalesList salesList, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
