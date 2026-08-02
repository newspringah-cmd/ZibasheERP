using FluentValidation;

namespace ZibasheERP.Application.Features.Shipments.CreateShipment;

public sealed class CreateShipmentValidator : AbstractValidator<CreateShipmentCommand>
{
    public CreateShipmentValidator()
    {
        RuleFor(command => command.OrderId).NotEmpty();
        RuleFor(command => command.AddressId).NotEmpty();
        RuleFor(command => command.ShippingCompany).NotEmpty().MaximumLength(100);
        RuleFor(command => command.ShippingCost).GreaterThanOrEqualTo(0);
        RuleFor(command => command.TrackingCode).NotEmpty().MaximumLength(100);
        RuleFor(command => command.Notes).MaximumLength(500);
    }
}
