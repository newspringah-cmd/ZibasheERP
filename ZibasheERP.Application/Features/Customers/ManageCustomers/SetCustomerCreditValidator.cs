using FluentValidation;

namespace ZibasheERP.Application.Features.Customers.ManageCustomers;

public sealed class SetCustomerCreditValidator : AbstractValidator<SetCustomerCreditCommand>
{
    public SetCustomerCreditValidator()
    {
        RuleFor(command => command.CustomerId).NotEmpty();
        RuleFor(command => command.CreditLimit).GreaterThanOrEqualTo(0);
        RuleFor(command => command.WalletBalance).GreaterThanOrEqualTo(0);
    }
}
