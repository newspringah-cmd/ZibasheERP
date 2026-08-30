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
    private readonly IInvoicePaymentAccountRepository _paymentAccountRepository;

    public IssueInvoiceCommandHandler(
        IInvoiceRepository invoiceRepository,
        INotificationOutboxRepository outboxRepository,
        IInvoicePaymentAccountRepository paymentAccountRepository)
    {
        _invoiceRepository = invoiceRepository;
        _outboxRepository = outboxRepository;
        _paymentAccountRepository = paymentAccountRepository;
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
        var paymentAccounts = await _paymentAccountRepository.GetActiveAsync(cancellationToken);
        var telegramGroup = order.Customer.TelegramGroup;
        var hasDeliveryGroup = telegramGroup is not null && !telegramGroup.IsDeleted &&
            telegramGroup.IsActive && !string.IsNullOrWhiteSpace(telegramGroup.ChatId);
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
            IssuedAt = now,
            DeliveryStatus = hasDeliveryGroup
                ? InvoiceDeliveryStatus.Pending
                : InvoiceDeliveryStatus.NeedsManualAction,
            DeliveryStatusChangedAt = now,
            DeliveryStatusNote = hasDeliveryGroup ? null : "گروه فعال مشتری برای ارسال فاکتور شناسایی نشده است."
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
                InvoiceId = invoice.Id,
                invoice.InvoiceNumber,
                invoice.IssuedAt,
                invoice.PerfumeTotal,
                invoice.BottleTotal,
                invoice.TotalAmount,
                CustomerUsername = order.Customer.Username,
                PaymentDeadlineHours = 24,
                PaymentAccounts = paymentAccounts.Select(value => new
                {
                    value.CardNumber, value.AccountHolder, value.BankName
                }),
                Items = order.Items.OrderBy(item => item.RowNumber).Select(item => new
                {
                    item.RowNumber,
                    PerfumePersianName = item.Perfume?.Name ?? item.ManualDescription,
                    PerfumeEnglishName = item.Perfume?.EnglishName ?? item.ManualDescription,
                    PerfumeBrand = item.Perfume?.Brand,
                    item.RequestedVolumeMl,
                    item.PerfumeAmount,
                    item.IsBottleOwner,
                    IsGift = item.SourceSalesListRequest?.IsGift == true,
                    GiftRecipientUsername = item.SourceSalesListRequest?.GiftRecipientTelegramUsername,
                    GiftRecipientTelegramId = item.SourceSalesListRequest?.GiftRecipientTelegramUserId,
                    BottleName = item.Bottle?.Name,
                    item.BottlePrice,
                    item.LineTotal
                })
            },
            now);
        if (notification is not null)
        {
            if (hasDeliveryGroup)
                notification.Recipient = telegramGroup!.ChatId.Trim();
            else
                notification = null;
        }
        if (notification is not null)
        {
            var sequence = 0;
            await _outboxRepository.AddAsync(new NotificationOutbox
            {
                Id = Guid.NewGuid(),
                CreatedAt = now.AddTicks(sequence++),
                CustomerId = order.CustomerId,
                OrderId = order.Id,
                Channel = "Telegram",
                EventType = "InvoiceGreeting",
                Recipient = notification.Recipient,
                Payload = "{}"
            }, cancellationToken);
            foreach (var photo in order.Items
                         .Where(item => item.SourceSalesListRequest?.IsGift != true &&
                                        !string.IsNullOrWhiteSpace(item.SalesList?.TelegramPhotoFileId))
                         .Select(item => new
                         {
                             FileId = item.SalesList!.TelegramPhotoFileId!,
                             PersianName = item.Perfume?.Name ?? item.ManualDescription,
                             EnglishName = item.Perfume?.EnglishName ?? item.ManualDescription
                         })
                         .GroupBy(value => value.FileId)
                         .Select(group => group.First()))
            {
                await _outboxRepository.AddAsync(new NotificationOutbox
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now.AddTicks(sequence++),
                    CustomerId = order.CustomerId,
                    OrderId = order.Id,
                    Channel = "Telegram",
                    EventType = "InvoicePerfumePhoto",
                    Recipient = notification.Recipient,
                    Payload = System.Text.Json.JsonSerializer.Serialize(photo)
                }, cancellationToken);
            }
        }
        if (!hasDeliveryGroup)
        {
            await _outboxRepository.AddAsync(new NotificationOutbox
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                CustomerId = order.CustomerId,
                OrderId = order.Id,
                Channel = "Telegram",
                EventType = "InvoiceDeliveryRequiresManualAction",
                Recipient = "admin",
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    order.Customer.Id,
                    order.Customer.FullName,
                    order.Customer.Username,
                    order.Customer.TelegramId,
                    order.OrderNumber,
                    invoice.InvoiceNumber
                })
            }, cancellationToken);
        }
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
                    ,order.Customer.Username
                },
                PaymentDeadlineHours = 24,
                PaymentAccounts = paymentAccounts.Select(value => new
                {
                    value.CardNumber, value.AccountHolder, value.BankName
                }),
                Items = order.Items.OrderBy(item => item.RowNumber).Select(item => new
                {
                    item.RowNumber,
                    PerfumePersianName = item.Perfume?.Name ?? item.ManualDescription,
                    PerfumeEnglishName = item.Perfume?.EnglishName ?? item.ManualDescription,
                    PerfumeBrand = item.Perfume?.Brand,
                    item.RequestedVolumeMl,
                    item.PerfumePricePerMl,
                    item.PerfumeAmount,
                    item.IsBottleOwner,
                    IsGift = item.SourceSalesListRequest?.IsGift == true,
                    GiftRecipientUsername = item.SourceSalesListRequest?.GiftRecipientTelegramUsername,
                    GiftRecipientTelegramId = item.SourceSalesListRequest?.GiftRecipientTelegramUserId,
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
            var number = $"INV-{now:yyMMdd}-{Random.Shared.Next(1000, 10000)}";
            if (!await _invoiceRepository.InvoiceNumberExistsAsync(number, cancellationToken))
                return number;
        }

        throw new InvalidOperationException("تولید شماره فاکتور یکتا ناموفق بود.");
    }
}
