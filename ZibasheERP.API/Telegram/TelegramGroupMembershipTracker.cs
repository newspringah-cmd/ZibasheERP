using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public interface ITelegramGroupMembershipTracker
{
    Task TrackAsync(TelegramChatMemberUpdated update, CancellationToken cancellationToken);
    Task MarkUnavailableAsync(string chatId, CancellationToken cancellationToken);
    Task<TelegramGroupLinkResult> LinkByInvoiceAsync(
        TelegramChat chat,
        string invoiceNumber,
        CancellationToken cancellationToken);
    Task<TelegramGroupLinkResult> LinkGiftRecipientByInvoiceAsync(
        TelegramChat chat,
        string invoiceNumber,
        string recipientIdentity,
        CancellationToken cancellationToken);
}

public enum TelegramGroupLinkStatus
{
    Linked,
    AlreadyLinked,
    InvoiceNotFound,
    GroupLinkedToAnotherCustomer,
    CustomerLinkedToAnotherGroup
}

public sealed record TelegramGroupLinkResult(
    TelegramGroupLinkStatus Status,
    string? CustomerName = null,
    int QueuedInvoiceCount = 0,
    int QueuedDecantPhotoCount = 0);

public sealed class TelegramGroupMembershipTracker(
    AppDbContext context,
    ILogger<TelegramGroupMembershipTracker> logger) : ITelegramGroupMembershipTracker
{
    public async Task TrackAsync(
        TelegramChatMemberUpdated update,
        CancellationToken cancellationToken)
    {
        if (!IsGroup(update.Chat.Type))
            return;

        var chatId = update.Chat.Id.ToString();
        var groups = await context.CustomerTelegramGroups
            .Where(value => value.ChatId == chatId && !value.IsDeleted)
            .ToArrayAsync(cancellationToken);
        if (groups.Length == 0)
        {
            logger.LogWarning(
                "Telegram bot membership changed for unmapped group {TelegramGroupChatId}.",
                chatId);
            return;
        }

        var now = DateTime.UtcNow;
        var canDeliver = TelegramGroupMembershipPolicy.CanDeliver(
            update.NewChatMember.Status,
            update.NewChatMember.IsMember,
            update.NewChatMember.CanSendMessages);
        foreach (var group in groups)
        {
            group.IsActive = canDeliver;
            if (!string.IsNullOrWhiteSpace(update.Chat.Title)) group.Title = update.Chat.Title.Trim();
            group.Username = NormalizeUsername(update.Chat.Username);
            group.LastSeenAt = now;
            group.UpdatedAt = now;
        }
        await context.SaveChangesAsync(cancellationToken);
        if (canDeliver)
        {
            var queued = 0;
            foreach (var customerId in groups.Select(value => value.CustomerId).Distinct())
                queued += await QueueUndeliveredDecantPhotosAsync(customerId, chatId, cancellationToken);
            if (queued > 0)
                logger.LogInformation(
                    "Queued {Count} deferred decant photos after Telegram bot joined group {TelegramGroupChatId}.",
                    queued,
                    chatId);
        }
    }

    public async Task MarkUnavailableAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        var groups = await context.CustomerTelegramGroups
            .Where(value => value.ChatId == chatId && !value.IsDeleted && value.IsActive)
            .ToArrayAsync(cancellationToken);
        if (groups.Length == 0)
            return;
        foreach (var group in groups)
        {
            group.IsActive = false;
            group.UpdatedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Telegram group {TelegramGroupChatId} was disabled after a permanent delivery failure.",
            chatId);
    }

    public async Task<TelegramGroupLinkResult> LinkByInvoiceAsync(
        TelegramChat chat,
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        var normalizedInvoiceNumber = invoiceNumber.Trim();
        var invoice = await context.Invoices
            .AsNoTracking()
            .Include(value => value.Order)
            .ThenInclude(value => value!.Customer)
            .FirstOrDefaultAsync(
                value => value.InvoiceNumber == normalizedInvoiceNumber && !value.IsDeleted,
                cancellationToken);
        var customer = invoice?.Order?.Customer;
        if (customer is null || invoice!.Order!.IsDeleted || customer.IsDeleted)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.InvoiceNotFound);

        var link = await LinkCustomerToChatAsync(customer, chat, cancellationToken);
        if (link.Status == TelegramGroupLinkStatus.CustomerLinkedToAnotherGroup) return link;
        var chatId = chat.Id.ToString();

        var queuedInvoiceCount = await QueueUndeliveredInvoicesAsync(
            customer.Id,
            chatId,
            cancellationToken);
        var queuedDecantPhotoCount = await QueueUndeliveredDecantPhotosAsync(
            customer.Id,
            chatId,
            cancellationToken);

        return new TelegramGroupLinkResult(
            link.Status,
            customer.FullName,
            queuedInvoiceCount,
            queuedDecantPhotoCount);
    }

    public async Task<TelegramGroupLinkResult> LinkGiftRecipientByInvoiceAsync(
        TelegramChat chat, string invoiceNumber, string recipientIdentity, CancellationToken cancellationToken)
    {
        var identity = recipientIdentity.Trim().TrimStart('@');
        var invoice = await context.Invoices
            .Include(value => value.Order).ThenInclude(value => value!.Items)
                .ThenInclude(value => value.SourceSalesListRequest)
            .Include(value => value.Order).ThenInclude(value => value!.Items)
                .ThenInclude(value => value.Perfume)
            .Include(value => value.Order).ThenInclude(value => value!.Items)
                .ThenInclude(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.InvoiceNumber == invoiceNumber.Trim() && !value.IsDeleted,
                cancellationToken);
        var order = invoice?.Order;
        var giftItems = order?.Items.Where(item => !item.IsDeleted && item.SourceSalesListRequest?.IsGift == true &&
            (string.Equals(item.SourceSalesListRequest.GiftRecipientTelegramUsername?.Trim().TrimStart('@'), identity,
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.SourceSalesListRequest.GiftRecipientTelegramUserId?.Trim(), identity,
                 StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        if (order is null || order.IsDeleted || giftItems.Length == 0)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.InvoiceNotFound);

        var request = giftItems[0].SourceSalesListRequest!;
        var customer = await FindOrCreateGiftRecipientAsync(request, cancellationToken);
        var link = await LinkCustomerToChatAsync(customer, chat, cancellationToken);
        if (link.Status == TelegramGroupLinkStatus.CustomerLinkedToAnotherGroup) return link;

        var now = DateTime.UtcNow;
        var sequence = 0;
        foreach (var item in giftItems)
        {
            if (!string.IsNullOrWhiteSpace(item.SalesList?.TelegramPhotoFileId))
                context.NotificationOutbox.Add(new NotificationOutbox
                {
                    Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++), CustomerId = customer.Id,
                    OrderId = order.Id, Channel = "Telegram", EventType = "InvoicePerfumePhoto",
                    Recipient = chat.Id.ToString(), Payload = JsonSerializer.Serialize(new
                    {
                        FileId = item.SalesList.TelegramPhotoFileId,
                        PersianName = item.Perfume?.Name ?? item.ManualDescription,
                        EnglishName = item.Perfume?.EnglishName ?? item.ManualDescription
                    })
                });
            context.NotificationOutbox.Add(new NotificationOutbox
            {
                Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++), CustomerId = customer.Id,
                OrderId = order.Id, Channel = "Telegram", EventType = "GiftInvoiceIssued",
                Recipient = chat.Id.ToString(), Payload = JsonSerializer.Serialize(new
                {
                    InvoiceNumber = invoice!.InvoiceNumber, IssuedAt = invoice.IssuedAt,
                    GiverUsername = request.TelegramUsername, GiverTelegramId = request.TelegramUserId,
                    PerfumePersianName = item.Perfume?.Name ?? item.ManualDescription,
                    PerfumeEnglishName = item.Perfume?.EnglishName ?? item.ManualDescription,
                    RequestedVolumeMl = item.RequestedVolumeMl
                })
            });
            context.NotificationOutbox.Add(N8nIntegrationEventFactory.CreateForDelivery(
                order, customer, chat.Id.ToString(), new
                {
                    OrderId = order.Id,
                    order.OrderNumber,
                    InvoiceId = (Guid?)null,
                    InvoiceNumber = invoice!.InvoiceNumber,
                    IssuedAt = invoice.IssuedAt,
                    PerfumeTotal = 0m,
                    BottleTotal = 0m,
                    TotalAmount = 0m,
                    Customer = new { customer.Id, customer.FullName, customer.Mobile, customer.TelegramId, customer.Username },
                    PaymentDeadlineHours = 0,
                    PaymentAccounts = Array.Empty<object>(),
                    Items = new[]
                    {
                        new
                        {
                            RowNumber = item.RowNumber,
                            PerfumePersianName = item.Perfume?.Name ?? item.ManualDescription,
                            PerfumeEnglishName = item.Perfume?.EnglishName ?? item.ManualDescription,
                            PerfumeBrand = item.Perfume?.Brand,
                            item.RequestedVolumeMl,
                            item.PerfumePricePerMl,
                            PerfumeAmount = 0m,
                            item.IsBottleOwner,
                            IsGift = true,
                            GiftRecipientUsername = request.GiftRecipientTelegramUsername,
                            GiftRecipientTelegramId = request.GiftRecipientTelegramUserId,
                            BottleName = item.Bottle?.Name,
                            BottlePrice = 0m,
                            LineTotal = 0m
                        }
                    }
                }, now.AddTicks(sequence++)));
        }
        await context.SaveChangesAsync(cancellationToken);
        return new TelegramGroupLinkResult(link.Status, customer.FullName, giftItems.Length);
    }

    private async Task<TelegramGroupLinkResult> LinkCustomerToChatAsync(
        Customer customer, TelegramChat chat, CancellationToken cancellationToken)
    {
        var chatId = chat.Id.ToString();
        var group = await context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.CustomerId == customer.Id && !value.IsDeleted, cancellationToken);
        if (group is not null && group.ChatId != chatId)
            return new TelegramGroupLinkResult(TelegramGroupLinkStatus.CustomerLinkedToAnotherGroup);
        var alreadyLinked = group is not null && group.IsActive;
        var now = DateTime.UtcNow;
        if (group is null)
        {
            group = new CustomerTelegramGroup { Id = Guid.NewGuid(), CustomerId = customer.Id,
                ChatId = chatId, CreatedAt = now, LinkedAt = now };
            context.CustomerTelegramGroups.Add(group);
        }
        group.Title = string.IsNullOrWhiteSpace(chat.Title) ? chatId : chat.Title.Trim();
        group.Username = NormalizeUsername(chat.Username);
        group.IsActive = true; group.IsDeleted = false; group.LastSeenAt = now; group.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);
        return new TelegramGroupLinkResult(alreadyLinked ? TelegramGroupLinkStatus.AlreadyLinked : TelegramGroupLinkStatus.Linked);
    }

    private async Task<Customer> FindOrCreateGiftRecipientAsync(SalesListRequest request, CancellationToken cancellationToken)
    {
        var username = request.GiftRecipientTelegramUsername?.Trim().TrimStart('@');
        var telegramId = request.GiftRecipientTelegramUserId?.Trim();
        var customer = await context.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            ((!string.IsNullOrWhiteSpace(telegramId) && value.TelegramId == telegramId) ||
             (!string.IsNullOrWhiteSpace(username) &&
              (value.Username == username || value.Username == $"@{username}"))), cancellationToken);
        if (customer is not null) return customer;
        var identity = username ?? telegramId ?? "gift-recipient";
        var mobile = $"TG-GIFT-{identity}";
        customer = new Customer { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            TelegramId = telegramId, Username = username, FullName = $"هدیه‌گیرنده {identity}",
            Mobile = mobile[..Math.Min(20, mobile.Length)],
            Notes = "مشتری به‌صورت خودکار از اتصال گروه هدیه ایجاد شد." };
        context.Customers.Add(customer);
        await context.SaveChangesAsync(cancellationToken);
        return customer;
    }

    private async Task<int> QueueUndeliveredDecantPhotosAsync(
        Guid customerId,
        string chatId,
        CancellationToken cancellationToken)
    {
        var notifications = await context.NotificationOutbox
            .Where(value => !value.IsDeleted && value.CustomerId == customerId &&
                value.Channel == "Telegram" && value.EventType == "DecantPhotoDelivery" &&
                value.Status == NotificationOutboxStatus.Failed)
            .ToArrayAsync(cancellationToken);
        if (notifications.Length == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var notification in notifications)
        {
            notification.Recipient = chatId;
            notification.Status = NotificationOutboxStatus.Pending;
            notification.Attempts = 0;
            notification.LastError = null;
            notification.LockedUntil = null;
            notification.NextAttemptAt = now;
            notification.UpdatedAt = now;
        }
        await context.SaveChangesAsync(cancellationToken);
        return notifications.Length;
    }

    private async Task<int> QueueUndeliveredInvoicesAsync(
        Guid customerId,
        string chatId,
        CancellationToken cancellationToken)
    {
        var invoices = await context.Invoices
            .Include(value => value.Order)
                .ThenInclude(value => value!.Customer)
                    .ThenInclude(value => value!.TelegramGroup)
            .Include(value => value.Order)
                .ThenInclude(value => value!.Items)
                    .ThenInclude(value => value.Perfume)
            .Include(value => value.Order)
                .ThenInclude(value => value!.Items)
                    .ThenInclude(value => value.Bottle)
            .Include(value => value.Order)
                .ThenInclude(value => value!.Items)
                    .ThenInclude(value => value.SalesList)
            .Where(value => !value.IsDeleted && value.Order != null && !value.Order.IsDeleted &&
                value.Order.CustomerId == customerId &&
                (value.DeliveryStatus == InvoiceDeliveryStatus.NeedsManualAction ||
                 value.DeliveryStatus == InvoiceDeliveryStatus.Failed))
            .OrderBy(value => value.IssuedAt)
            .ToArrayAsync(cancellationToken);
        if (invoices.Length == 0)
            return 0;

        var orderIds = invoices.Select(value => value.OrderId).ToArray();
        var alreadyQueuedOrderIds = await context.NotificationOutbox.AsNoTracking()
            .Where(value => !value.IsDeleted && value.Channel == "N8n" &&
                value.EventType == "InvoiceIssued" && value.OrderId.HasValue &&
                orderIds.Contains(value.OrderId.Value) &&
                (value.Status == NotificationOutboxStatus.Pending ||
                 value.Status == NotificationOutboxStatus.Processing))
            .Select(value => value.OrderId!.Value)
            .ToArrayAsync(cancellationToken);
        var queuedSet = alreadyQueuedOrderIds.ToHashSet();
        var paymentAccounts = await context.InvoicePaymentAccounts.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive)
            .OrderBy(value => value.DisplayOrder)
            .Select(value => new { value.CardNumber, value.AccountHolder, value.BankName })
            .ToArrayAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var queued = 0;

        foreach (var invoice in invoices.Where(value => !queuedSet.Contains(value.OrderId)))
        {
            var order = invoice.Order!;
            var sequence = 0;
            context.NotificationOutbox.Add(new NotificationOutbox
            {
                Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++),
                CustomerId = order.CustomerId, OrderId = order.Id,
                Channel = "Telegram", EventType = "InvoiceGreeting", Recipient = chatId,
                Payload = "{}"
            });
            foreach (var photo in order.Items
                         .Where(item => !string.IsNullOrWhiteSpace(item.SalesList?.TelegramPhotoFileId))
                         .Select(item => new
                         {
                             FileId = item.SalesList!.TelegramPhotoFileId!,
                             PersianName = item.Perfume?.Name ?? item.ManualDescription,
                             EnglishName = item.Perfume?.EnglishName ?? item.ManualDescription
                         })
                         .GroupBy(value => value.FileId)
                         .Select(group => group.First()))
            {
                context.NotificationOutbox.Add(new NotificationOutbox
                {
                    Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++),
                    CustomerId = order.CustomerId, OrderId = order.Id,
                    Channel = "Telegram", EventType = "InvoicePerfumePhoto", Recipient = chatId,
                    Payload = JsonSerializer.Serialize(photo)
                });
            }
            context.NotificationOutbox.Add(N8nIntegrationEventFactory.Create(
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
                        order.Customer!.Id,
                        order.Customer.FullName,
                        order.Customer.Mobile,
                        order.Customer.TelegramId,
                        order.Customer.Username
                    },
                    PaymentDeadlineHours = 24,
                    PaymentAccounts = paymentAccounts,
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
                        BottleName = item.Bottle?.Name,
                        item.BottlePrice,
                        item.LineTotal
                    })
                },
                now.AddTicks(sequence)));
            invoice.DeliveryStatus = InvoiceDeliveryStatus.RetryScheduled;
            invoice.DeliveryStatusChangedAt = now;
            invoice.DeliveryStatusNote = "ارسال مجدد پس از اتصال گروه مشتری در صف قرار گرفت.";
            invoice.UpdatedAt = now;
            queued++;
        }

        if (queued > 0)
            await context.SaveChangesAsync(cancellationToken);
        return queued;
    }

    private static bool IsGroup(string chatType) =>
        string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(chatType, "supergroup", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeUsername(string? username)
    {
        var normalized = username?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
