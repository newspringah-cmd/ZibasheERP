using MediatR;

namespace ZibasheERP.Application.Features.Invoices.IssueInvoice;

public sealed record IssueInvoiceCommand(
    Guid OrderId,
    IReadOnlyCollection<string>? ManualProductPhotoFileIds = null,
    bool IsGift = false,
    string? GiftRecipientUsername = null,
    string? GiftRecipientTelegramId = null) : IRequest<InvoiceResponse>;
