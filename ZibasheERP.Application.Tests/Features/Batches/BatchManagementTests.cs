using ZibasheERP.Application.Features.Batches.CreateBatch;
using ZibasheERP.Application.Features.Batches.GetBatches;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Batches;

public sealed class BatchManagementTests
{
    [Fact]
    public async Task CreateBatch_SetsRemainingVolumeToTotalVolume()
    {
        var perfume = CreatePerfume();
        var batches = new BatchRepositoryStub();
        var handler = new CreateBatchCommandHandler(
            batches,
            new PerfumeRepositoryStub(perfume));

        var result = await handler.Handle(
            new CreateBatchCommand(
                perfume.Id,
                "  BATCH-100  ",
                20_000_000,
                100,
                DateTime.UtcNow),
            CancellationToken.None);

        Assert.NotNull(batches.AddedBatch);
        Assert.Equal("BATCH-100", batches.AddedBatch!.BatchNumber);
        Assert.Equal(100m, batches.AddedBatch.RemainingVolumeMl);
        Assert.Equal(100m, result.RemainingVolumeMl);
        Assert.True(batches.SaveChangesCalled);
    }

    [Fact]
    public async Task CreateBatch_RejectsInactivePerfume()
    {
        var perfume = CreatePerfume();
        perfume.IsActive = false;
        var handler = new CreateBatchCommandHandler(
            new BatchRepositoryStub(),
            new PerfumeRepositoryStub(perfume));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new CreateBatchCommand(
                    perfume.Id,
                    "BATCH-101",
                    20_000_000,
                    100,
                    DateTime.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetBatches_ReturnsPerfumeDetails()
    {
        var perfume = CreatePerfume();
        var batch = new Batch
        {
            Id = Guid.NewGuid(),
            PerfumeId = perfume.Id,
            Perfume = perfume,
            BatchNumber = "BATCH-102",
            TotalVolumeMl = 100,
            RemainingVolumeMl = 75
        };
        var handler = new GetBatchesQueryHandler(new BatchRepositoryStub(batch));

        var result = await handler.Handle(new GetBatchesQuery(), CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal("Test Brand", result.Single().Brand);
        Assert.Equal(75m, result.Single().RemainingVolumeMl);
    }

    private static Perfume CreatePerfume() => new()
    {
        Id = Guid.NewGuid(),
        Name = "عطر تست",
        EnglishName = "Test Perfume",
        Brand = "Test Brand",
        IsActive = true
    };

    private sealed class BatchRepositoryStub(params Batch[] batches) : IBatchRepository
    {
        public Batch? AddedBatch { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public Task<IReadOnlyCollection<Batch>> GetForInventoryAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Batch>>(batches.Take(limit).ToArray());
        public Task<Batch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(batches.FirstOrDefault(batch => batch.Id == id));
        public Task<bool> BatchNumberExistsAsync(string batchNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(batches.Any(batch => batch.BatchNumber == batchNumber));
        public Task AddAsync(Batch batch, CancellationToken cancellationToken = default)
        {
            AddedBatch = batch;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Batch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PerfumeRepositoryStub(Perfume perfume) : IPerfumeRepository
    {
        public Task<Perfume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Perfume?>(perfume.Id == id ? perfume : null);
        public Task<IReadOnlyCollection<Perfume>> GetAllAsync(bool includeInactive, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Perfume>>(new[] { perfume });
        public Task<bool> ExistsAsync(string brand, string englishName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task AddAsync(Perfume value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(Perfume value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
