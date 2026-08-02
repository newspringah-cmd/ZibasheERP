using FluentValidation;

namespace ZibasheERP.Application.Features.Perfumes.ManagePerfume;

public sealed class UpdatePerfumePriceValidator : AbstractValidator<UpdatePerfumePriceCommand>
{
    public UpdatePerfumePriceValidator()
    {
        RuleFor(command => command.PerfumeId).NotEmpty();
        RuleFor(command => command.PricePerMl).GreaterThan(0);
    }
}
