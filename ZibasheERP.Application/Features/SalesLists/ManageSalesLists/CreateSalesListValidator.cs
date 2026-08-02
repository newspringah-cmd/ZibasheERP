using FluentValidation;

namespace ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

public sealed class CreateSalesListValidator : AbstractValidator<CreateSalesListCommand>
{
    public CreateSalesListValidator()
    {
        RuleFor(command => command.BatchId).NotEmpty();
        RuleFor(command => command.PricePerMl).GreaterThan(0);
        RuleFor(command => command.TotalVolume).InclusiveBetween(1, 5000);
        RuleFor(command => command.TelegramChannelId).MaximumLength(100);
        RuleFor(command => command.Notes).MaximumLength(500);
    }
}
