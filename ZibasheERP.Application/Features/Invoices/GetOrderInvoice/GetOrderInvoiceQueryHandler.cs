using MediatR;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.Application.Features.Invoices.GetOrderInvoice;

public sealed class GetOrderInvoiceQueryHandler
    : IRequestHandler<GetOrderInvoiceQuery, InvoiceResponse?>
{
    private readonly IInvoiceRepository _invoiceRepository;

    public GetOrderInvoiceQueryHandler(IInvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public async Task<InvoiceResponse?> Handle(
        GetOrderInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var invoice = await _invoiceRepository.GetByOrderIdAsync(
            request.OrderId,
            cancellationToken);
        return invoice is null ? null : InvoiceResponse.FromEntity(invoice);
    }
}
