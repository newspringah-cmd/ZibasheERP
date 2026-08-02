using MediatR;

namespace ZibasheERP.Application.Features.Invoices.GetInvoice;

public sealed record GetInvoiceQuery(Guid InvoiceId) : IRequest<InvoiceResponse?>;
