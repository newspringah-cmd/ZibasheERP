using FluentValidation;

namespace ZibasheERP.Application.Features.Perfumes.CreatePerfume;

public sealed class CreatePerfumeValidator : AbstractValidator<CreatePerfumeCommand>
{
    public CreatePerfumeValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        RuleFor(command => command.EnglishName).NotEmpty().MaximumLength(200);
        RuleFor(command => command.Brand).NotEmpty().MaximumLength(150);
        RuleFor(command => command.PricePerMl).GreaterThan(0);
        RuleFor(command => command.OriginalBottleVolumeMl).InclusiveBetween(10, 5000);
        RuleFor(command => command.Notes).MaximumLength(500);
    }
}
