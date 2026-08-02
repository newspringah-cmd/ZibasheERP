using ZibasheERP.Application.Features.Bottles.ManageBottles;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Bottles;

public sealed class BottleManagementTests
{
    [Fact]
    public async Task CreateBottle_PersistsActiveBottle()
    {
        var repository = new BottleRepositoryStub();
        var handler = new CreateBottleCommandHandler(repository);

        var result = await handler.Handle(
            new CreateBottleCommand("  Fancy 20  ", 20, BottleType.Fancy, 150_000, true, null),
            CancellationToken.None);

        Assert.NotNull(repository.AddedBottle);
        Assert.Equal("Fancy 20", repository.AddedBottle!.Name);
        Assert.True(result.IsActive);
        Assert.True(result.IsDefault);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task CreateBottle_RejectsSecondDefaultForSameConfiguration()
    {
        var repository = new BottleRepositoryStub { DefaultExists = true };
        var handler = new CreateBottleCommandHandler(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new CreateBottleCommand("Fancy 20", 20, BottleType.Fancy, 150_000, true, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task SetBottleStatus_DeactivatesBottle()
    {
        var bottle = new Bottle
        {
            Id = Guid.NewGuid(),
            Name = "Bottle",
            VolumeMl = 10,
            Type = BottleType.Normal,
            IsActive = true
        };
        var repository = new BottleRepositoryStub(bottle);
        var handler = new SetBottleStatusCommandHandler(repository);

        var result = await handler.Handle(
            new SetBottleStatusCommand(bottle.Id, false),
            CancellationToken.None);

        Assert.False(bottle.IsActive);
        Assert.False(result.IsActive);
        Assert.True(repository.SaveChangesCalled);
    }

    private sealed class BottleRepositoryStub(params Bottle[] bottles) : IBottleRepository
    {
        public Bottle? AddedBottle { get; private set; }
        public bool SaveChangesCalled { get; private set; }
        public bool DefaultExists { get; init; }
        public Task<Bottle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.FirstOrDefault(bottle => bottle.Id == id && bottle.IsActive));
        public Task<Bottle?> GetByTypeAsync(BottleType type, CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.FirstOrDefault(bottle => bottle.Type == type && bottle.IsActive));
        public Task<List<Bottle>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.Where(bottle => bottle.IsActive).ToList());
        public Task<IReadOnlyCollection<Bottle>> GetForAdminAsync(bool includeInactive, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Bottle>>(bottles.Where(bottle => includeInactive || bottle.IsActive).Take(limit).ToArray());
        public Task<Bottle?> GetForAdminByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.FirstOrDefault(bottle => bottle.Id == id));
        public Task<bool> ExistsAsync(string name, int volumeMl, BottleType type, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DefaultExistsAsync(int volumeMl, BottleType type, CancellationToken cancellationToken = default) => Task.FromResult(DefaultExists);
        public Task AddAsync(Bottle bottle, CancellationToken cancellationToken = default)
        {
            AddedBottle = bottle;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Bottle bottle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
