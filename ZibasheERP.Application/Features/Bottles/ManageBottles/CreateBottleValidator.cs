using FluentValidation;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Bottles.ManageBottles;

public sealed class CreateBottleValidator : AbstractValidator<CreateBottleCommand>
{
    public CreateBottleValidator()
    {
        RuleFor(command => command.Name).NotEmpty().MaximumLength(150);
        RuleFor(command => command.VolumeMl).InclusiveBetween(1, 1000);
        RuleFor(command => command.Type).IsInEnum().NotEqual((BottleType)0);
        RuleFor(command => command.SalePrice).GreaterThanOrEqualTo(0);
        RuleFor(command => command.Notes).MaximumLength(500);
    }
}
