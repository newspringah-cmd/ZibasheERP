using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Invoices.GetInvoice;

public sealed class GetInvoiceQueryHandler
    : IRequestHandler<GetInvoiceQuery, InvoiceResponse?>
{
    private readonly IInvoiceRepository _invoiceRepository;

    public GetInvoiceQueryHandler(IInvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public async Task<InvoiceResponse?> Handle(
        GetInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(
            request.InvoiceId,
            cancellationToken);
        return invoice is null ? null : InvoiceResponse.FromEntity(invoice);
    }
}
