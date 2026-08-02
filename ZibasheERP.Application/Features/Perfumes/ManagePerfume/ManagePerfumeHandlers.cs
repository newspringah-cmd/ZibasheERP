using MediatR;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Perfumes.ManagePerfume;

public sealed class SetPerfumeStatusCommandHandler
    : IRequestHandler<SetPerfumeStatusCommand, PerfumeResponse>
{
    private readonly IPerfumeRepository _repository;

    public SetPerfumeStatusCommandHandler(IPerfumeRepository repository)
    {
        _repository = repository;
    }

    public async Task<PerfumeResponse> Handle(
        SetPerfumeStatusCommand request,
        CancellationToken cancellationToken)
    {
        var perfume = await _repository.GetByIdAsync(request.PerfumeId, cancellationToken)
            ?? throw new InvalidOperationException("عطر پیدا نشد.");
        perfume.IsActive = request.IsActive;
        perfume.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(perfume, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return CreatePerfumeCommandHandler.ToResponse(perfume);
    }
}

public sealed class UpdatePerfumePriceCommandHandler
    : IRequestHandler<UpdatePerfumePriceCommand, PerfumeResponse>
{
    private readonly IPerfumeRepository _repository;

    public UpdatePerfumePriceCommandHandler(IPerfumeRepository repository)
    {
        _repository = repository;
    }

    public async Task<PerfumeResponse> Handle(
        UpdatePerfumePriceCommand request,
        CancellationToken cancellationToken)
    {
        var perfume = await _repository.GetByIdAsync(request.PerfumeId, cancellationToken)
            ?? throw new InvalidOperationException("عطر پیدا نشد.");
        perfume.PricePerMl = request.PricePerMl;
        perfume.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(perfume, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return CreatePerfumeCommandHandler.ToResponse(perfume);
    }
}
