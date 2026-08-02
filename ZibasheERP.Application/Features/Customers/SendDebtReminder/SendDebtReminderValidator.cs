using FluentValidation;

namespace ZibasheERP.Application.Features.Customers.SendDebtReminder;

public sealed class SendDebtReminderValidator : AbstractValidator<SendDebtReminderCommand>
{
    public SendDebtReminderValidator()
    {
        RuleFor(command => command.CustomerId).NotEmpty();
        RuleFor(command => command.Message).MaximumLength(300);
    }
}
