using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Bottles.ManageBottles;

public sealed class CreateBottleCommandHandler
    : IRequestHandler<CreateBottleCommand, AdminBottleResponse>
{
    private readonly IBottleRepository _repository;

    public CreateBottleCommandHandler(IBottleRepository repository)
    {
        _repository = repository;
    }

    public async Task<AdminBottleResponse> Handle(
        CreateBottleCommand request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await _repository.ExistsAsync(name, request.VolumeMl, request.Type, cancellationToken))
            throw new InvalidOperationException("این بطری با همین نام، حجم و نوع قبلاً ثبت شده است.");
        if (request.IsDefault && await _repository.DefaultExistsAsync(request.VolumeMl, request.Type, cancellationToken))
            throw new InvalidOperationException("برای این حجم و نوع، بطری پیش‌فرض دیگری وجود دارد.");

        var bottle = new Bottle
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            Name = name,
            VolumeMl = request.VolumeMl,
            Type = request.Type,
            SalePrice = request.SalePrice,
            IsDefault = request.IsDefault,
            IsActive = true,
            Notes = NormalizeOptional(request.Notes)
        };
        await _repository.AddAsync(bottle, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return BottleMapper.ToResponse(bottle);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SetBottleStatusCommandHandler
    : IRequestHandler<SetBottleStatusCommand, AdminBottleResponse>
{
    private readonly IBottleRepository _repository;

    public SetBottleStatusCommandHandler(IBottleRepository repository)
    {
        _repository = repository;
    }

    public async Task<AdminBottleResponse> Handle(
        SetBottleStatusCommand request,
        CancellationToken cancellationToken)
    {
        var bottle = await _repository.GetForAdminByIdAsync(request.BottleId, cancellationToken)
            ?? throw new InvalidOperationException("بطری پیدا نشد.");
        bottle.IsActive = request.IsActive;
        bottle.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(bottle, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return BottleMapper.ToResponse(bottle);
    }
}

public sealed class GetAdminBottlesQueryHandler
    : IRequestHandler<GetAdminBottlesQuery, IReadOnlyCollection<AdminBottleResponse>>
{
    private readonly IBottleRepository _repository;

    public GetAdminBottlesQueryHandler(IBottleRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyCollection<AdminBottleResponse>> Handle(
        GetAdminBottlesQuery request,
        CancellationToken cancellationToken)
    {
        var bottles = await _repository.GetForAdminAsync(
            request.IncludeInactive,
            Math.Clamp(request.Limit, 1, 200),
            cancellationToken);
        return bottles.Select(BottleMapper.ToResponse).ToArray();
    }
}

internal static class BottleMapper
{
    internal static AdminBottleResponse ToResponse(Bottle bottle) => new(
        bottle.Id,
        bottle.Name,
        bottle.VolumeMl,
        bottle.Type.ToString(),
        bottle.SalePrice,
        bottle.IsDefault,
        bottle.IsActive,
        bottle.Notes);
}
