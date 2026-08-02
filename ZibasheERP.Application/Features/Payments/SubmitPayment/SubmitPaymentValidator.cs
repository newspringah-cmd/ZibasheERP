using FluentValidation;

namespace ZibasheERP.Application.Features.Payments.SubmitPayment;

public sealed class SubmitPaymentValidator : AbstractValidator<SubmitPaymentCommand>
{
    public SubmitPaymentValidator()
    {
        RuleFor(command => command.OrderId).NotEmpty();
        RuleFor(command => command.Amount).GreaterThan(0);
        RuleFor(command => command.PaymentMethod).NotEmpty().MaximumLength(50);
        RuleFor(command => command.TransactionId).NotEmpty().MaximumLength(100);
        RuleFor(command => command.Notes).MaximumLength(500);
    }
}
