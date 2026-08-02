using FluentValidation;

namespace ZibasheERP.Application.Features.Payments.ConfirmPayment;

public sealed class ConfirmPaymentValidator : AbstractValidator<ConfirmPaymentCommand>
{
    public ConfirmPaymentValidator()
    {
        RuleFor(command => command.PaymentId).NotEmpty();
    }
}
