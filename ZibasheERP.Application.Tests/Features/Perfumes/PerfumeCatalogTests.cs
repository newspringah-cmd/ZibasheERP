using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using ZibasheERP.Application.Features.Perfumes.GetPerfumes;
using ZibasheERP.Application.Features.Perfumes.ManagePerfume;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Perfumes;

public sealed class PerfumeCatalogTests
{
    [Fact]
    public async Task CreatePerfume_NormalizesAndPersistsPerfume()
    {
        var repository = new PerfumeRepositoryStub();
        var handler = new CreatePerfumeCommandHandler(repository);

        var result = await handler.Handle(
            new CreatePerfumeCommand(
                "  عطر تست  ",
                "  Test Perfume  ",
                "  Test Brand  ",
                250_000,
                100,
                "  Sample note  "),
            CancellationToken.None);

        Assert.NotNull(repository.AddedPerfume);
        Assert.Equal("عطر تست", repository.AddedPerfume!.Name);
        Assert.Equal("Test Perfume", result.EnglishName);
        Assert.Equal("Test Brand", result.Brand);
        Assert.True(result.IsActive);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task CreatePerfume_RejectsDuplicateBrandAndEnglishName()
    {
        var repository = new PerfumeRepositoryStub
        {
            DuplicateExists = true
        };
        var handler = new CreatePerfumeCommandHandler(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new CreatePerfumeCommand(
                    "عطر تست",
                    "Test Perfume",
                    "Test Brand",
                    250_000,
                    100,
                    null),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetPerfumes_ReturnsCatalogResponses()
    {
        var perfume = new Perfume
        {
            Id = Guid.NewGuid(),
            Name = "عطر تست",
            EnglishName = "Test Perfume",
            Brand = "Test Brand",
            PricePerMl = 250_000,
            OriginalBottleVolumeMl = 100,
            IsActive = true
        };
        var repository = new PerfumeRepositoryStub(perfume);
        var handler = new GetPerfumesQueryHandler(repository);

        var result = await handler.Handle(
            new GetPerfumesQuery(),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal(perfume.Id, result.Single().Id);
        Assert.Equal("Test Brand", result.Single().Brand);
    }

    [Fact]
    public async Task SetPerfumeStatus_DeactivatesPerfume()
    {
        var perfume = CreateExistingPerfume();
        var repository = new PerfumeRepositoryStub(perfume);
        var handler = new SetPerfumeStatusCommandHandler(repository);

        var result = await handler.Handle(
            new SetPerfumeStatusCommand(perfume.Id, false),
            CancellationToken.None);

        Assert.False(perfume.IsActive);
        Assert.False(result.IsActive);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task UpdatePerfumePrice_ChangesOnlyCurrentCatalogPrice()
    {
        var perfume = CreateExistingPerfume();
        var repository = new PerfumeRepositoryStub(perfume);
        var handler = new UpdatePerfumePriceCommandHandler(repository);

        var result = await handler.Handle(
            new UpdatePerfumePriceCommand(perfume.Id, 275_000),
            CancellationToken.None);

        Assert.Equal(275_000m, perfume.PricePerMl);
        Assert.Equal(275_000m, result.PricePerMl);
        Assert.True(repository.SaveChangesCalled);
    }

    private static Perfume CreateExistingPerfume() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Perfume",
        EnglishName = "Test Perfume",
        Brand = "Test Brand",
        PricePerMl = 250_000,
        OriginalBottleVolumeMl = 100,
        IsActive = true
    };

    private sealed class PerfumeRepositoryStub(params Perfume[] perfumes) : IPerfumeRepository
    {
        public Perfume? AddedPerfume { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public bool DuplicateExists { get; init; }

        public Task<IReadOnlyCollection<Perfume>> GetAllAsync(
            bool includeInactive,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Perfume>>(
                perfumes.Where(perfume => includeInactive || perfume.IsActive).Take(limit).ToArray());

        public Task<Perfume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(perfumes.FirstOrDefault(perfume => perfume.Id == id));

        public Task<bool> ExistsAsync(
            string brand,
            string englishName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DuplicateExists);

        public Task AddAsync(Perfume perfume, CancellationToken cancellationToken = default)
        {
            AddedPerfume = perfume;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Perfume perfume, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
