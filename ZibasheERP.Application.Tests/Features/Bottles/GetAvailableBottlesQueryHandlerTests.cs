using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Bottles;

public sealed class GetAvailableBottlesQueryHandlerTests
{
    [Fact]
    public async Task Handle_FiltersByExactVolumeAndBottleRules()
    {
        var expected = new Bottle { Id = Guid.NewGuid(), Name = "Fancy 20", VolumeMl = 20, Type = BottleType.Fancy };
        var handler = new GetAvailableBottlesQueryHandler(new BottleRepositoryStub(
            expected,
            new Bottle { VolumeMl = 20, Type = BottleType.Normal },
            new Bottle { VolumeMl = 10, Type = BottleType.Fancy }));

        var result = await handler.Handle(new GetAvailableBottlesQuery(20), CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal(expected.Id, result.Single().Id);
    }

    private sealed class BottleRepositoryStub(params Bottle[] bottles) : IBottleRepository
    {
        public Task<List<Bottle>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bottles.ToList());
        public Task<Bottle?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Bottle?>(bottles.FirstOrDefault(value => value.Id == id));
        public Task<Bottle?> GetByTypeAsync(BottleType type, CancellationToken cancellationToken = default) =>
            Task.FromResult<Bottle?>(bottles.FirstOrDefault(value => value.Type == type));
    }
}
