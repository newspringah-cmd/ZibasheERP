using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Invoices.IssueInvoice;

public sealed class IssueInvoiceCommandHandler
    : IRequestHandler<IssueInvoiceCommand, InvoiceResponse>
{
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public IssueInvoiceCommandHandler(
        IInvoiceRepository invoiceRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _invoiceRepository = invoiceRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<InvoiceResponse> Handle(
        IssueInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        var existing = await _invoiceRepository.GetByOrderIdAsync(
            request.OrderId,
            cancellationToken);
        if (existing is not null)
            return InvoiceResponse.FromEntity(existing);

        var order = await _invoiceRepository.GetOrderForInvoiceAsync(
            request.OrderId,
            cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");

        if (order.Status == OrderState.Cancelled)
            throw new InvalidOperationException("برای سفارش لغوشده نمی‌توان فاکتور صادر کرد.");

        if (order.Customer is null || order.Items.Count == 0)
            throw new InvalidOperationException("اطلاعات سفارش برای صدور فاکتور کامل نیست.");

        var now = DateTime.UtcNow;
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            OrderId = order.Id,
            Order = order,
            InvoiceNumber = await GenerateInvoiceNumberAsync(now, cancellationToken),
            Status = InvoiceStatus.Issued,
            PerfumeTotal = order.PerfumeTotal,
            BottleTotal = order.BottleTotal,
            TotalAmount = order.FinalAmount,
            IssuedAt = now
        };

        if (order.Status != OrderState.Paid)
            order.Status = OrderState.Invoiced;
        order.InvoiceIssuedAt = now;
        order.UpdatedAt = now;

        await _invoiceRepository.AddAsync(invoice, cancellationToken);
        var notification = TelegramNotificationFactory.Create(
            order,
            "InvoiceIssued",
            new
            {
                order.Id,
                order.OrderNumber,
                invoice.InvoiceNumber,
                invoice.TotalAmount
            },
            now);
        if (notification is not null)
            await _outboxRepository.AddAsync(notification, cancellationToken);
        var integrationEvent = N8nIntegrationEventFactory.Create(
            order,
            "InvoiceIssued",
            new
            {
                OrderId = order.Id,
                order.OrderNumber,
                InvoiceId = invoice.Id,
                invoice.InvoiceNumber,
                invoice.IssuedAt,
                invoice.PerfumeTotal,
                invoice.BottleTotal,
                invoice.TotalAmount,
                Customer = new
                {
                    order.Customer.Id,
                    order.Customer.FullName,
                    order.Customer.Mobile,
                    order.Customer.TelegramId
                },
                Items = order.Items.OrderBy(item => item.RowNumber).Select(item => new
                {
                    item.RowNumber,
                    PerfumeName = item.Perfume?.Name,
                    PerfumeBrand = item.Perfume?.Brand,
                    item.RequestedVolumeMl,
                    item.PerfumePricePerMl,
                    item.PerfumeAmount,
                    item.IsBottleOwner,
                    BottleName = item.Bottle?.Name,
                    item.BottlePrice,
                    item.LineTotal
                })
            },
            now);
        await _outboxRepository.AddAsync(integrationEvent, cancellationToken);
        await _invoiceRepository.SaveChangesAsync(cancellationToken);

        return InvoiceResponse.FromEntity(invoice);
    }

    private async Task<string> GenerateInvoiceNumberAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var number = $"INV-{now:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 10000)}";
            if (!await _invoiceRepository.InvoiceNumberExistsAsync(number, cancellationToken))
                return number;
        }

        throw new InvalidOperationException("تولید شماره فاکتور یکتا ناموفق بود.");
    }
}
