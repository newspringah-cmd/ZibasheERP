using ZibasheERP.Application.Features.Inventory.GetInventory;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Inventory;

public sealed class GetInventoryQueryHandlerTests
{
    [Fact]
    public async Task Handle_CalculatesInventorySummaryAndBatchUtilization()
    {
        var perfume = new Perfume
        {
            Id = Guid.NewGuid(),
            Name = "Test Perfume",
            EnglishName = "Test",
            Brand = "Test Brand",
            PricePerMl = 250_000
        };
        var batches = new[]
        {
            new Batch
            {
                Id = Guid.NewGuid(),
                PerfumeId = perfume.Id,
                Perfume = perfume,
                BatchNumber = "B-001",
                TotalVolumeMl = 100,
                RemainingVolumeMl = 65,
                Status = "Available"
            }
        };
        var bottles = new[]
        {
            new Bottle
            {
                Id = Guid.NewGuid(),
                Name = "10 ml",
                VolumeMl = 10,
                Type = BottleType.Normal,
                SalePrice = 50_000,
                IsActive = true
            }
        };
        var handler = new GetInventoryQueryHandler(
            new BatchRepositoryStub(batches),
            new BottleRepositoryStub(bottles));

        var result = await handler.Handle(
            new GetInventoryQuery(),
            CancellationToken.None);

        Assert.Equal(1, result.Summary.BatchCount);
        Assert.Equal(1, result.Summary.PerfumeCount);
        Assert.Equal(100m, result.Summary.TotalVolumeMl);
        Assert.Equal(65m, result.Summary.RemainingVolumeMl);
        Assert.Equal(35m, result.Summary.ReservedOrConsumedVolumeMl);
        Assert.Equal(35m, result.Batches.Single().UsedVolumeMl);
        Assert.Equal(65m, result.Batches.Single().RemainingPercentage);
        Assert.Equal(1, result.Summary.ActiveBottleCount);
    }

    private sealed class BatchRepositoryStub(IReadOnlyCollection<Batch> batches) : IBatchRepository
    {
        public Task<IReadOnlyCollection<Batch>> GetForInventoryAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Batch>>(batches.Take(limit).ToArray());
        public Task<Batch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(batches.FirstOrDefault(batch => batch.Id == id));
        public Task<bool> BatchNumberExistsAsync(string batchNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(batches.Any(batch => batch.BatchNumber == batchNumber));
        public Task AddAsync(Batch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Batch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BottleRepositoryStub(IReadOnlyCollection<Bottle> bottles) : IBottleRepository
    {
        public Task<Bottle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.FirstOrDefault(bottle => bottle.Id == id));
        public Task<Bottle?> GetByTypeAsync(BottleType type, CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.FirstOrDefault(bottle => bottle.Type == type));
        public Task<List<Bottle>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.Where(bottle => bottle.IsActive).ToList());
        public Task<IReadOnlyCollection<Bottle>> GetForAdminAsync(bool includeInactive, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Bottle>>(bottles.Take(limit).ToArray());
        public Task<Bottle?> GetForAdminByIdAsync(Guid id, CancellationToken cancellationToken = default) => GetByIdAsync(id, cancellationToken);
        public Task<bool> ExistsAsync(string name, int volumeMl, BottleType type, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DefaultExistsAsync(int volumeMl, BottleType type, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Bottle bottle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Bottle bottle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
