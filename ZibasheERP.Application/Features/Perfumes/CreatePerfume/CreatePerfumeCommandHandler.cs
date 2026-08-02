using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Perfumes.CreatePerfume;

public sealed class CreatePerfumeCommandHandler
    : IRequestHandler<CreatePerfumeCommand, PerfumeResponse>
{
    private readonly IPerfumeRepository _repository;

    public CreatePerfumeCommandHandler(IPerfumeRepository repository)
    {
        _repository = repository;
    }

    public async Task<PerfumeResponse> Handle(
        CreatePerfumeCommand request,
        CancellationToken cancellationToken)
    {
        var brand = request.Brand.Trim();
        var englishName = request.EnglishName.Trim();
        if (await _repository.ExistsAsync(brand, englishName, cancellationToken))
            throw new InvalidOperationException("این عطر از همین برند قبلاً ثبت شده است.");

        var perfume = new Perfume
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            Name = request.Name.Trim(),
            EnglishName = englishName,
            Brand = brand,
            PricePerMl = request.PricePerMl,
            OriginalBottleVolumeMl = request.OriginalBottleVolumeMl,
            IsActive = true,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };

        await _repository.AddAsync(perfume, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        return ToResponse(perfume);
    }

    internal static PerfumeResponse ToResponse(Perfume perfume) => new(
        perfume.Id,
        perfume.Name,
        perfume.EnglishName,
        perfume.Brand,
        perfume.PricePerMl,
        perfume.OriginalBottleVolumeMl,
        perfume.IsActive,
        perfume.Notes);
}
