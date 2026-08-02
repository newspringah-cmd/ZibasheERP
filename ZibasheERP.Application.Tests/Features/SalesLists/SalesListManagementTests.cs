using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.SalesLists;

public sealed class SalesListManagementTests
{
    [Fact]
    public async Task CreateSalesList_OpensListFromAvailableBatch()
    {
        var batch = CreateBatch();
        var lists = new SalesListRepositoryStub();
        var handler = new CreateSalesListCommandHandler(lists, new BatchRepositoryStub(batch));

        var result = await handler.Handle(
            new CreateSalesListCommand(batch.Id, 300_000, 100, "@channel", null),
            CancellationToken.None);

        Assert.NotNull(lists.AddedSalesList);
        Assert.Equal(SalesListStatus.Open, lists.AddedSalesList!.Status);
        Assert.Equal(100, result.RemainingVolume);
        Assert.Equal("Test Brand", result.Brand);
        Assert.True(lists.SaveChangesCalled);
    }

    [Fact]
    public async Task CreateSalesList_RejectsVolumeAboveBatchInventory()
    {
        var batch = CreateBatch();
        batch.RemainingVolumeMl = 50;
        var handler = new CreateSalesListCommandHandler(
            new SalesListRepositoryStub(),
            new BatchRepositoryStub(batch));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new CreateSalesListCommand(batch.Id, 300_000, 100, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task CloseSalesList_StopsNewOrdersAndKeepsReservations()
    {
        var batch = CreateBatch();
        var list = new SalesList
        {
            Id = Guid.NewGuid(),
            BatchId = batch.Id,
            Batch = batch,
            TotalVolume = 100,
            ReservedVolume = 40,
            Status = SalesListStatus.Open
        };
        var repository = new SalesListRepositoryStub(list);
        var handler = new CloseSalesListCommandHandler(repository);

        var result = await handler.Handle(new CloseSalesListCommand(list.Id), CancellationToken.None);

        Assert.Equal(SalesListStatus.Closed, list.Status);
        Assert.Equal(40, result.ReservedVolume);
        Assert.NotNull(list.ClosedDate);
        Assert.True(repository.SaveChangesCalled);
    }

    private static Batch CreateBatch()
    {
        var perfume = new Perfume
        {
            Id = Guid.NewGuid(),
            Name = "Test Perfume",
            Brand = "Test Brand",
            IsActive = true
        };
        return new Batch
        {
            Id = Guid.NewGuid(),
            PerfumeId = perfume.Id,
            Perfume = perfume,
            BatchNumber = "BATCH-SALES",
            TotalVolumeMl = 100,
            RemainingVolumeMl = 100
        };
    }

    private sealed class SalesListRepositoryStub(params SalesList[] lists) : ISalesListRepository
    {
        public SalesList? AddedSalesList { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public Task<SalesList?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(lists.FirstOrDefault(list => list.Id == id));
        public Task<IReadOnlyCollection<SalesList>> GetOpenAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<SalesList>>(lists.Where(list => list.Status == SalesListStatus.Open).Take(limit).ToArray());
        public Task<IReadOnlyCollection<SalesList>> GetForAdminAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<SalesList>>(lists.Take(limit).ToArray());
        public Task<bool> HasActiveForBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(lists.Any(list => list.BatchId == batchId && list.Status == SalesListStatus.Open));
        public Task AddAsync(SalesList salesList, CancellationToken cancellationToken = default)
        {
            AddedSalesList = salesList;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(SalesList salesList, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class BatchRepositoryStub(Batch batch) : IBatchRepository
    {
        public Task<IReadOnlyCollection<Batch>> GetForInventoryAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Batch>>(new[] { batch });
        public Task<Batch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Batch?>(batch.Id == id ? batch : null);
        public Task<bool> BatchNumberExistsAsync(string batchNumber, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Batch value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Batch value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
