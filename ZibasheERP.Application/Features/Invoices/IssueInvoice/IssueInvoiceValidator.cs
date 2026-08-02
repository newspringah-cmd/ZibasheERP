using FluentValidation;

namespace ZibasheERP.Application.Features.Invoices.IssueInvoice;

public sealed class IssueInvoiceValidator : AbstractValidator<IssueInvoiceCommand>
{
    public IssueInvoiceValidator()
    {
        RuleFor(command => command.OrderId).NotEmpty();
    }
}
