using MediatR;

namespace ZibasheERP.Application.Features.Invoices.IssueInvoice;

public sealed record IssueInvoiceCommand(
    Guid OrderId,
    string? ManualProductPhotoFileId = null) : IRequest<InvoiceResponse>;
