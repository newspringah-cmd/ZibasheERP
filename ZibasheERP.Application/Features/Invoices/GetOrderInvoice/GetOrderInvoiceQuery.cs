using MediatR;

namespace ZibasheERP.Application.Features.Invoices.GetOrderInvoice;

public sealed record GetOrderInvoiceQuery(Guid OrderId) : IRequest<InvoiceResponse?>;
