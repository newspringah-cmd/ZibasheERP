using MediatR;

namespace ZibasheERP.Application.Features.Invoices.IssueInvoice;

public sealed record IssueInvoiceCommand(Guid OrderId) : IRequest<InvoiceResponse>;
