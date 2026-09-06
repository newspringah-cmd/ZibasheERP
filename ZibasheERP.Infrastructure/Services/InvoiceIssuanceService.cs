using MediatR;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Invoices.IssueInvoice;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class InvoiceIssuanceService : IInvoiceIssuanceService
{
    private readonly AppDbContext _db;
    private readonly ISender _sender;

    public InvoiceIssuanceService(AppDbContext db, ISender sender)
    {
        _db = db;
        _sender = sender;
    }

    public async Task<IReadOnlyCollection<CompletedSalesListForInvoice>> GetCompletedListsAsync(
        int limit, CancellationToken cancellationToken = default) =>
        await _db.SalesLists.AsNoTracking()
            .Where(list => !list.IsDeleted &&
                (list.Status == SalesListStatus.Closed || list.Status == SalesListStatus.Full) &&
                !_db.InvoiceIssuanceBatchSalesLists.Any(link => link.SalesListId == list.Id))
            .OrderBy(list => list.ClosedDate ?? list.OpenDate)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(list => new CompletedSalesListForInvoice(
                list.Id, list.PublicCode, list.EnglishName,
                list.Requests.Count(request => !request.IsDeleted &&
                    request.Kind == SalesListRequestKind.CurrentBottle &&
                    request.Status == SalesListRequestStatus.Confirmed),
                list.TotalVolume))
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<CompletedSalesListForInvoice>> GetWaitingListsAsync(
        int limit, CancellationToken cancellationToken = default) =>
        await _db.SalesLists.AsNoTracking()
            .Where(list => !list.IsDeleted &&
                list.Status == SalesListStatus.AwaitingAvailability &&
                !_db.InvoiceIssuanceBatchSalesLists.Any(link => link.SalesListId == list.Id))
            .OrderBy(list => list.UpdatedAt ?? list.ClosedDate ?? list.OpenDate)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(list => new CompletedSalesListForInvoice(
                list.Id, list.PublicCode, list.EnglishName,
                list.Requests.Count(request => !request.IsDeleted &&
                    request.Kind == SalesListRequestKind.CurrentBottle &&
                    request.Status == SalesListRequestStatus.Confirmed),
                list.TotalVolume))
            .ToArrayAsync(cancellationToken);

    public async Task MoveCompletedListToWaitingAsync(
        Guid salesListId, CancellationToken cancellationToken = default)
    {
        var list = await GetUnassignedListAsync(salesListId, cancellationToken);
        if (list.Status is not (SalesListStatus.Closed or SalesListStatus.Full))
            throw new InvalidOperationException("فقط لیست تکمیل‌شده را می‌توان به مخزن انتظار منتقل کرد.");
        list.Status = SalesListStatus.AwaitingAvailability;
        list.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreWaitingListAsync(
        Guid salesListId, CancellationToken cancellationToken = default)
    {
        var list = await GetUnassignedListAsync(salesListId, cancellationToken);
        if (list.Status != SalesListStatus.AwaitingAvailability)
            throw new InvalidOperationException("این لیست در مخزن انتظار نیست.");
        list.Status = list.RemainingVolume == 0 ? SalesListStatus.Full : SalesListStatus.Closed;
        list.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelCompletedListAsync(
        Guid salesListId, CancellationToken cancellationToken = default)
    {
        var list = await GetUnassignedListAsync(salesListId, cancellationToken);
        if (list.Status is not (SalesListStatus.Closed or SalesListStatus.Full or SalesListStatus.AwaitingAvailability))
            throw new InvalidOperationException("این لیست دیگر امکان حذف از صف صدور فاکتور را ندارد.");
        list.Status = SalesListStatus.Cancelled;
        list.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceIssuanceResult> IssueCompletedListsAsync(
        IReadOnlyCollection<Guid> salesListIds,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var ids = salesListIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            throw new InvalidOperationException("حداقل یک لیست تکمیل‌شده را انتخاب کنید.");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var lists = await _db.SalesLists
            .Include(list => list.Requests.Where(request => !request.IsDeleted &&
                request.Kind == SalesListRequestKind.CurrentBottle &&
                request.Status == SalesListRequestStatus.Confirmed))
                .ThenInclude(request => request.Bottle)
            .Include(list => list.Perfume)
            .Where(list => ids.Contains(list.Id) && !list.IsDeleted)
            .ToArrayAsync(cancellationToken);
        if (lists.Length != ids.Length)
            throw new InvalidOperationException("یکی از لیست‌های انتخاب‌شده پیدا نشد.");
        if (lists.Any(list => list.Status is not (SalesListStatus.Closed or SalesListStatus.Full)))
            throw new InvalidOperationException("فقط لیست‌های تکمیل‌شده امکان صدور فاکتور دارند.");
        if (await _db.InvoiceIssuanceBatchSalesLists.AnyAsync(link => ids.Contains(link.SalesListId), cancellationToken))
            throw new InvalidOperationException("حداقل یکی از لیست‌ها قبلاً وارد نوبت صدور فاکتور شده است.");

        var requests = lists.SelectMany(list => list.Requests.Select(request => (List: list, Request: request))).ToArray();
        if (requests.Length == 0)
            throw new InvalidOperationException("برای لیست‌های انتخاب‌شده درخواست تأییدشده‌ای وجود ندارد.");

        // درخواست‌های قدیمیِ واردشده پیش از ثبت نوع شیشه ممکن است BottleId نداشته باشند.
        // تنها fallback امن، شیشهٔ نرمالِ پیش‌فرضِ همان حجم است؛ شیشهٔ فانتزی هرگز حدسی نرمال نمی‌شود.
        var missingBottleVolumes = requests
            .Select(value => value.Request)
            .Where(request => !request.IsBottleOwner && !request.BottleId.HasValue)
            .Select(request => request.VolumeMl)
            .Distinct()
            .ToArray();
        if (missingBottleVolumes.Length > 0)
        {
            var defaultNormalBottles = await _db.Bottles
                .Where(bottle => !bottle.IsDeleted && bottle.IsActive && bottle.IsDefault &&
                    bottle.Type == BottleType.Normal && missingBottleVolumes.Contains(bottle.VolumeMl))
                .ToDictionaryAsync(bottle => bottle.VolumeMl, cancellationToken);
            foreach (var request in requests.Select(value => value.Request)
                         .Where(request => !request.IsBottleOwner && !request.BottleId.HasValue))
            {
                if (!defaultNormalBottles.TryGetValue(request.VolumeMl, out var bottle))
                    continue;
                request.BottleId = bottle.Id;
                request.Bottle = bottle;
                if (request.BottlePrice <= 0)
                    request.BottlePrice = bottle.SalePrice;
                request.UpdatedAt = DateTime.UtcNow;
            }
        }

        var now = DateTime.UtcNow;
        var productionCopies = lists.Select(CreateProductionCopy).ToArray();
        var batch = new InvoiceIssuanceBatch
        {
            Id = Guid.NewGuid(), CreatedAt = now,
            CreatedByTelegramUserId = issuedByTelegramUserId.Trim(),
            Status = InvoiceIssuanceBatchStatus.Issuing
        };
        foreach (var list in lists)
        {
            batch.SalesLists.Add(new InvoiceIssuanceBatchSalesList
            {
                InvoiceIssuanceBatchId = batch.Id, SalesListId = list.Id
            });
            list.Status = SalesListStatus.QueuedForInvoice;
            list.UpdatedAt = now;
        }
        await _db.InvoiceIssuanceBatches.AddAsync(batch, cancellationToken);

        var orders = new List<Order>();
        foreach (var customerRequests in requests.GroupBy(value => CustomerKey(value.Request)))
        {
            var customer = await ResolveCustomerAsync(customerRequests.First().Request, cancellationToken);
            var order = new Order
            {
                Id = Guid.NewGuid(), CreatedAt = now, CustomerId = customer.Id,
                OrderNumber = await GenerateOrderNumberAsync(now, cancellationToken),
                Status = OrderStatus.ListCompleted, RegisteredAt = now,
                Source = OrderSource.SalesListInvoice, InvoiceIssuanceBatchId = batch.Id,
                Notes = $"فاکتور تجمیعی لیست‌ها: {string.Join("، ", customerRequests.Select(value => value.List.PublicCode).Distinct().Order())}"
            };
            var row = 0;
            foreach (var (list, request) in customerRequests.OrderBy(value => value.List.OpenDate).ThenBy(value => value.Request.ConfirmedAt))
            {
                row++;
                var perfumeAmount = request.PerfumePricePerMl * request.VolumeMl;
                var bottleAmount = ResolveInvoiceBottleAmount(request, list.PublicCode);
                order.Items.Add(new OrderItem
                {
                    Id = Guid.NewGuid(), CreatedAt = now, OrderId = order.Id,
                    SalesListId = list.Id, PerfumeId = list.PerfumeId,
                    SourceSalesListRequestId = request.Id,
                    SourceSalesListRequest = request,
                    RequestedVolumeMl = request.VolumeMl, Quantity = 1,
                    PerfumePricePerMl = request.PerfumePricePerMl,
                    PerfumeAmount = perfumeAmount, IsBottleOwner = request.IsBottleOwner,
                    BottleId = request.BottleId, BottlePrice = bottleAmount,
                    LineTotal = perfumeAmount + bottleAmount, RowNumber = row,
                    Notes = $"کد لیست {list.PublicCode}"
                });
                request.Status = SalesListRequestStatus.Invoiced;
            }
            order.PerfumeTotal = order.Items.Sum(item => item.PerfumeAmount);
            order.BottleTotal = order.Items.Sum(item => item.BottlePrice);
            order.FinalAmount = order.Items.Sum(item => item.LineTotal);
            customer.CurrentDebt += order.FinalAmount;
            customer.LastOrderAt = now;
            customer.UpdatedAt = now;
            orders.Add(order);
        }

        _db.Orders.AddRange(orders);
        await _db.SaveChangesAsync(cancellationToken);
        var invoiceNumbers = new List<string>();
        foreach (var order in orders)
        {
            var invoice = await _sender.Send(new IssueInvoiceCommand(order.Id), cancellationToken);
            invoiceNumbers.Add(invoice.InvoiceNumber);
            await QueueGiftRecipientNotificationsAsync(order, invoice.InvoiceNumber, cancellationToken);
        }
        foreach (var list in lists)
        {
            list.Status = SalesListStatus.Invoiced;
            list.UpdatedAt = DateTime.UtcNow;
        }
        batch.Status = InvoiceIssuanceBatchStatus.Issued;
        batch.IssuedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InvoiceIssuanceResult(batch.Id, orders.Count, invoiceNumbers, productionCopies);
    }

    private static decimal ResolveInvoiceBottleAmount(SalesListRequest request, int publicCode)
    {
        if (request.IsBottleOwner)
            return 0m;

        if (request.BottlePrice > 0)
            return request.BottlePrice;

        if (request.Bottle?.SalePrice is > 0)
        {
            // Imported legacy requests may have a selected bottle but no saved price.
            // Snapshot the current bottle price so future retries produce the same invoice amount.
            request.BottlePrice = request.Bottle.SalePrice;
            return request.BottlePrice;
        }

        throw new BottlePriceResolutionRequiredException(
            request.Id, publicCode, RequestIdentity(request), request.Bottle?.Name ?? "شیشه نامشخص");
    }

    private static string RequestIdentity(SalesListRequest request) =>
        !string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"@{request.TelegramUsername.Trim().TrimStart('@')}"
            : request.TelegramUserId;

    private async Task QueueGiftRecipientNotificationsAsync(
        Order order,
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        var giftItems = order.Items
            .Where(item => item.SourceSalesListRequest?.IsGift == true)
            .OrderBy(item => item.RowNumber)
            .ToArray();
        if (giftItems.Length == 0)
            return;

        var now = DateTime.UtcNow;
        var sequence = 0;
        foreach (var item in giftItems)
        {
            var request = item.SourceSalesListRequest!;
            var recipientTelegramId = request.GiftRecipientTelegramUserId?.Trim();
            var recipientUsername = request.GiftRecipientTelegramUsername?.Trim().TrimStart('@');
            var recipientWithAt = string.IsNullOrWhiteSpace(recipientUsername) ? null : $"@{recipientUsername}";
            var recipient = await _db.Customers.AsNoTracking()
                .Include(customer => customer.TelegramGroup)
                .FirstOrDefaultAsync(customer => !customer.IsDeleted &&
                    (customer.TelegramId == recipientTelegramId ||
                     (!string.IsNullOrWhiteSpace(recipientUsername) &&
                      (customer.Username == recipientUsername || customer.Username == recipientWithAt))),
                    cancellationToken);
            var group = recipient?.TelegramGroup;
            var hasGroup = group is not null && !group.IsDeleted && group.IsActive &&
                           !string.IsNullOrWhiteSpace(group.ChatId);

            if (!hasGroup)
            {
                await _db.NotificationOutbox.AddAsync(new NotificationOutbox
                {
                    Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++),
                    CustomerId = recipient?.Id ?? order.CustomerId, OrderId = order.Id,
                    Channel = "Telegram", EventType = "InvoiceGiftDeliveryRequiresManualAction",
                    Recipient = "admin",
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        invoiceNumber,
                        RecipientUsername = recipientUsername,
                        RecipientTelegramId = recipientTelegramId,
                        GiverUsername = request.TelegramUsername,
                        GiverTelegramId = request.TelegramUserId
                    })
                }, cancellationToken);
                continue;
            }

            var chatId = group!.ChatId.Trim();
            if (!string.IsNullOrWhiteSpace(item.SalesList?.TelegramPhotoFileId))
            {
                await _db.NotificationOutbox.AddAsync(new NotificationOutbox
                {
                    Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++),
                    CustomerId = recipient!.Id, OrderId = order.Id,
                    Channel = "Telegram", EventType = "InvoicePerfumePhoto", Recipient = chatId,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        FileId = item.SalesList.TelegramPhotoFileId,
                        PersianName = item.Perfume?.Name ?? item.ManualDescription,
                        EnglishName = item.Perfume?.EnglishName ?? item.ManualDescription
                    })
                }, cancellationToken);
            }

            await _db.NotificationOutbox.AddAsync(new NotificationOutbox
            {
                Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++),
                CustomerId = recipient!.Id, OrderId = order.Id,
                Channel = "Telegram", EventType = "GiftInvoiceIssued", Recipient = chatId,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    InvoiceNumber = invoiceNumber,
                    IssuedAt = now,
                    GiverUsername = request.TelegramUsername,
                    GiverTelegramId = request.TelegramUserId,
                    PerfumePersianName = item.Perfume?.Name ?? item.ManualDescription,
                    PerfumeEnglishName = item.Perfume?.EnglishName ?? item.ManualDescription,
                    item.RequestedVolumeMl,
                    TotalAmount = 0
                })
            }, cancellationToken);
            await _db.NotificationOutbox.AddAsync(N8nIntegrationEventFactory.CreateForDelivery(
                order, recipient!, chatId, new
                {
                    OrderId = order.Id,
                    order.OrderNumber,
                    InvoiceId = (Guid?)null,
                    InvoiceNumber = invoiceNumber,
                    IssuedAt = now,
                    PerfumeTotal = 0,
                    BottleTotal = 0,
                    TotalAmount = 0,
                    Customer = new
                    {
                        recipient.Id, recipient.FullName, recipient.Mobile,
                        recipient.TelegramId, recipient.Username
                    },
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
                            GiftRecipientUsername = recipientUsername,
                            GiftRecipientTelegramId = recipientTelegramId,
                            BottleName = item.Bottle?.Name,
                            BottlePrice = 0m,
                            LineTotal = 0m
                        }
                    }
                }, now.AddTicks(sequence++)), cancellationToken);
        }
    }

    public async Task<InvoiceIssuanceResult> IssueManualAsync(
        string customerIdentity,
        IReadOnlyCollection<ManualInvoiceLineInput> lines,
        IReadOnlyCollection<string> productPhotoFileIds,
        string issuedByTelegramUserId,
        string? giftRecipientIdentity = null,
        CancellationToken cancellationToken = default)
    {
        var identity = customerIdentity.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidOperationException("شناسه مشتری لازم است.");
        var validLines = lines.Where(line => !string.IsNullOrWhiteSpace(line.Description) &&
                                             line.Quantity > 0 && line.UnitAmount >= 0 && line.BottleAmount >= 0).ToArray();
        if (validLines.Length == 0)
            throw new InvalidOperationException("حداقل یک ردیف معتبر برای فاکتور دستی لازم است.");
        var validPhotoFileIds = productPhotoFileIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (validPhotoFileIds.Length != validLines.Length)
            throw new InvalidOperationException("برای هر آیتم فاکتور دستی باید یک عکس محصول ثبت شود.");

        var customer = await ResolveManualInvoiceCustomerAsync(identity, cancellationToken);
        Customer? giftRecipient = null;
        if (!string.IsNullOrWhiteSpace(giftRecipientIdentity))
        {
            var recipientIdentity = giftRecipientIdentity.Trim().TrimStart('@');
            var recipientUsernameWithAt = $"@{recipientIdentity}";
            giftRecipient = await _db.Customers.Include(value => value.TelegramGroup)
                .FirstOrDefaultAsync(value => !value.IsDeleted &&
                    (value.TelegramId == recipientIdentity || value.Username == recipientIdentity ||
                     value.Username == recipientUsernameWithAt), cancellationToken)
                ?? throw new InvalidOperationException("هدیه‌گیرنده پیدا نشد؛ ابتدا او را با Telegram ID یا @username شناسایی کنید.");
            if (giftRecipient.Id == customer.Id)
                throw new InvalidOperationException("هدیه‌دهنده و هدیه‌گیرنده نمی‌توانند یک نفر باشند.");
        }
        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), CreatedAt = now, CustomerId = customer.Id,
            OrderNumber = await GenerateOrderNumberAsync(now, cancellationToken),
            Status = OrderStatus.Registered, RegisteredAt = now, Source = OrderSource.ManualInvoice,
            Notes = $"فاکتور دستی توسط {issuedByTelegramUserId.Trim()}" +
                    (giftRecipient is null ? string.Empty : $" | هدیه برای {giftRecipient.Username ?? giftRecipient.TelegramId}")
        };
        var row = 0;
        foreach (var line in validLines)
        {
            row++;
            var perfumeAmount = line.Quantity * line.UnitAmount;
            order.Items.Add(new OrderItem
            {
                Id = Guid.NewGuid(), CreatedAt = now, OrderId = order.Id,
                ManualDescription = line.Description.Trim(), RequestedVolumeMl = line.Quantity,
                Quantity = 1, PerfumePricePerMl = line.UnitAmount,
                PerfumeAmount = perfumeAmount, BottlePrice = line.BottleAmount,
                LineTotal = perfumeAmount + line.BottleAmount, RowNumber = row
            });
        }
        order.PerfumeTotal = order.Items.Sum(item => item.PerfumeAmount);
        order.BottleTotal = order.Items.Sum(item => item.BottlePrice);
        order.FinalAmount = order.Items.Sum(item => item.LineTotal);
        customer.CurrentDebt += order.FinalAmount;
        customer.LastOrderAt = now;
        customer.UpdatedAt = now;
        await _db.Orders.AddAsync(order, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var invoice = await _sender.Send(
            new IssueInvoiceCommand(order.Id, validPhotoFileIds, giftRecipient is not null),
            cancellationToken);
        if (giftRecipient is not null)
            await QueueManualGiftRecipientNotificationsAsync(order, invoice, giftRecipient, validPhotoFileIds, cancellationToken);
        return new InvoiceIssuanceResult(Guid.Empty, 1, new[] { invoice.InvoiceNumber }, Array.Empty<SalesListProductionCopy>());
    }

    private async Task QueueManualGiftRecipientNotificationsAsync(
        Order order, ZibasheERP.Application.Features.Invoices.InvoiceResponse invoice,
        Customer recipient, IReadOnlyCollection<string> photoFileIds, CancellationToken cancellationToken)
    {
        var group = recipient.TelegramGroup;
        if (group is null || group.IsDeleted || !group.IsActive || string.IsNullOrWhiteSpace(group.ChatId))
        {
            await _db.NotificationOutbox.AddAsync(new NotificationOutbox
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CustomerId = recipient.Id, OrderId = order.Id,
                Channel = "Telegram", EventType = "InvoiceGiftDeliveryRequiresManualAction", Recipient = "admin",
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    invoice.InvoiceNumber, RecipientUsername = recipient.Username,
                    RecipientTelegramId = recipient.TelegramId, IsManualGift = true
                })
            }, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        var chatId = group.ChatId.Trim();
        var sequence = 0;
        foreach (var photoFileId in photoFileIds)
        {
            await _db.NotificationOutbox.AddAsync(new NotificationOutbox
            {
                Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++), CustomerId = recipient.Id, OrderId = order.Id,
                Channel = "Telegram", EventType = "InvoicePerfumePhoto", Recipient = chatId,
                Payload = System.Text.Json.JsonSerializer.Serialize(new { FileId = photoFileId })
            }, cancellationToken);
        }
        await _db.NotificationOutbox.AddAsync(new NotificationOutbox
        {
            Id = Guid.NewGuid(), CreatedAt = now.AddTicks(sequence++), CustomerId = recipient.Id, OrderId = order.Id,
            Channel = "Telegram", EventType = "GiftInvoiceIssued", Recipient = chatId,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                invoice.InvoiceNumber, invoice.IssuedAt,
                PerfumePersianName = string.Join("، ", invoice.Items.Select(item => item.PerfumeName)),
                RequestedVolumeMl = invoice.Items.Sum(item => item.VolumeMl), TotalAmount = 0
            })
        }, cancellationToken);
        await _db.NotificationOutbox.AddAsync(N8nIntegrationEventFactory.CreateForDelivery(order, recipient, chatId, new
        {
            OrderId = order.Id, order.OrderNumber, InvoiceId = (Guid?)null,
            invoice.InvoiceNumber, invoice.IssuedAt, PerfumeTotal = 0m, BottleTotal = 0m, TotalAmount = 0m,
            Customer = new { recipient.Id, recipient.FullName, recipient.Mobile, recipient.TelegramId, recipient.Username },
            PaymentDeadlineHours = 0, PaymentAccounts = Array.Empty<object>(),
            Items = invoice.Items.Select((item, index) => new
            {
                RowNumber = index + 1, PerfumePersianName = item.PerfumeName,
                PerfumeEnglishName = item.PerfumeName, PerfumeBrand = item.PerfumeBrand,
                RequestedVolumeMl = item.VolumeMl, PerfumePricePerMl = item.PricePerMl,
                PerfumeAmount = 0m, item.IsBottleOwner, IsGift = true,
                GiftRecipientUsername = recipient.Username, GiftRecipientTelegramId = recipient.TelegramId,
                item.BottleName, BottlePrice = 0m, LineTotal = 0m
            })
        }, now.AddTicks(sequence++)), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> ResolveManualInvoiceCustomerAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var username = identity.Trim().TrimStart('@');
        var usernameWithAt = $"@{username}";
        var customer = _db.Customers.Local.FirstOrDefault(value => !value.IsDeleted &&
            (value.TelegramId == username || value.Username == username || value.Username == usernameWithAt));
        if (customer is not null)
            return customer;

        customer = await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            (value.TelegramId == username || value.Username == username || value.Username == usernameWithAt),
            cancellationToken);
        if (customer is not null)
            return customer;

        var isTelegramId = username.All(char.IsDigit);
        customer = new Customer
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            TelegramId = isTelegramId ? username : null,
            Username = isTelegramId ? null : username,
            FullName = isTelegramId ? $"مشتری تلگرام {username}" : $"@{username}",
            Mobile = $"TG-MANUAL-{Guid.NewGuid():N}"[..20],
            Notes = "مشتری به‌صورت خودکار از فاکتور دستی ایجاد شد؛ گروه تلگرام برای ارسال فاکتور هنوز متصل نیست."
        };
        await _db.Customers.AddAsync(customer, cancellationToken);
        return customer;
    }

    public async Task<IReadOnlyCollection<InvoicePaymentTrackingReport>> GetPaymentTrackingReportsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.InvoiceIssuanceBatches.AsNoTracking()
            .Include(value => value.SalesLists)
                .ThenInclude(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == batchId && !value.IsDeleted, cancellationToken);
        if (batch is null)
            return Array.Empty<InvoicePaymentTrackingReport>();

        var orders = await _db.Orders.AsNoTracking()
            .Include(value => value.Customer)
            .Include(value => value.Items.Where(item => !item.IsDeleted))
                .ThenInclude(item => item.SalesList)
            .Where(value => value.InvoiceIssuanceBatchId == batchId && !value.IsDeleted)
            .OrderBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);
        var orderIds = orders.Select(value => value.Id).ToArray();
        var invoices = await _db.Invoices.AsNoTracking()
            .Where(value => orderIds.Contains(value.OrderId) && !value.IsDeleted)
            .ToDictionaryAsync(value => value.OrderId, cancellationToken);
        return batch.SalesLists
            .OrderBy(link => link.SalesList.PublicCode)
            .Select(link =>
            {
                var listOrders = orders
                    .Where(order => order.Items.Any(item => item.SalesListId == link.SalesListId))
                    .ToArray();
                var rows = listOrders.Select((order, index) =>
                {
                    invoices.TryGetValue(order.Id, out var invoice);
                    var identity = !string.IsNullOrWhiteSpace(order.Customer?.Username)
                        ? $"@{order.Customer.Username.TrimStart('@')}"
                        : order.Customer?.TelegramId ?? order.Customer?.FullName ?? "مشتری نامشخص";
                    var paid = invoice?.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Paid &&
                               order.Status == OrderStatus.Paid;
                    var listAmount = order.Items
                        .Where(item => item.SalesListId == link.SalesListId)
                        .Sum(item => item.LineTotal);
                    return $"{(paid ? "✅" : "🔴")} {index + 1}. {identity} — " +
                           $"{invoice?.InvoiceNumber ?? "بدون فاکتور"} — {listAmount:N0} تومان";
                });
                var message = $"💳 واریز جدید\n" +
                              $"عطر: {link.SalesList.EnglishName}\n" +
                              $"کد لیست: {link.SalesList.PublicCode}\n" +
                              $"تعداد فاکتور: {listOrders.Length}\n\n{string.Join("\n", rows)}\n\n" +
                              $"✅ پرداخت‌شده   🔴 در انتظار پرداخت\nآخرین بروزرسانی: {DateTime.UtcNow.AddHours(3.5):yyyy/MM/dd HH:mm}";
                var actions = listOrders.Where(order =>
                        invoices.TryGetValue(order.Id, out var invoice) &&
                        invoice.Status != ZibasheERP.Domain.Enums.InvoiceStatus.Paid &&
                        order.Status != OrderStatus.Paid)
                    .SelectMany(order => order.Items
                        .Where(item => item.SalesListId == link.SalesListId)
                        .Select(item => new InvoicePaymentTrackingAction(
                            item.Id,
                            $"📤 {(order.Customer?.Username ?? order.Customer?.TelegramId ?? "مشتری").TrimStart('@')} | " +
                            $"{item.SalesList?.EnglishName ?? item.ManualDescription ?? "آیتم"} | {item.RequestedVolumeMl}ml")))
                    .ToArray();
                return new InvoicePaymentTrackingReport(
                    batch.Id, link.SalesListId, message,
                    link.TelegramPaymentTrackingChatId,
                    link.TelegramPaymentTrackingMessageId, actions);
            })
            .ToArray();
    }

    public async Task SetPaymentTrackingMessageAsync(
        Guid batchId,
        Guid salesListId,
        string chatId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        var link = await _db.InvoiceIssuanceBatchSalesLists.FirstOrDefaultAsync(
            value => value.InvoiceIssuanceBatchId == batchId && value.SalesListId == salesListId,
            cancellationToken) ?? throw new InvalidOperationException("نوبت صدور فاکتور پیدا نشد.");
        link.TelegramPaymentTrackingChatId = chatId.Trim();
        link.TelegramPaymentTrackingMessageId = messageId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> ResolveCustomerAsync(SalesListRequest request, CancellationToken cancellationToken)
    {
        var telegramId = request.TelegramUserId.Trim();
        var username = request.TelegramUsername?.Trim().TrimStart('@');
        var normalizedUsername = NormalizeCustomerUsername(username);

        // A batch can contain legacy requests for the same username with different
        // TelegramUserId values. Include newly tracked customers in the lookup so a
        // second group in the same SaveChanges does not create a duplicate username.
        var customer = _db.Customers.Local.FirstOrDefault(value => !value.IsDeleted &&
            (value.TelegramId == telegramId ||
             (!string.IsNullOrWhiteSpace(normalizedUsername) &&
              NormalizeCustomerUsername(value.Username) == normalizedUsername)));
        if (customer is not null) return customer;

        var usernameWithAt = string.IsNullOrWhiteSpace(normalizedUsername) ? null : $"@{normalizedUsername}";
        customer = await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            (value.TelegramId == telegramId || (!string.IsNullOrWhiteSpace(normalizedUsername) &&
                                                 (value.Username!.ToLower() == normalizedUsername ||
                                                  value.Username.ToLower() == usernameWithAt))), cancellationToken);
        if (customer is not null) return customer;

        var customerId = Guid.NewGuid();
        customer = new Customer
        {
            Id = customerId, CreatedAt = DateTime.UtcNow,
            TelegramId = telegramId, Username = username,
            FullName = string.IsNullOrWhiteSpace(username) ? $"مشتری تلگرام {telegramId}" : $"@{username}",
            // Imported/admin identities can share a long common prefix (for example
            // "admin-username:"). Truncating that identity produced duplicate mobile
            // values. Use the entity id so every placeholder remains unique.
            Mobile = $"TG-{customerId:N}"[..20],
            Notes = "مشتری به‌صورت خودکار از درخواست فروش‌لیست ایجاد شد؛ اطلاعات تماس نیازمند تکمیل است."
        };
        await _db.Customers.AddAsync(customer, cancellationToken);
        return customer;
    }

    private async Task<SalesList> GetUnassignedListAsync(
        Guid salesListId, CancellationToken cancellationToken)
    {
        var list = await _db.SalesLists.FirstOrDefaultAsync(
            value => value.Id == salesListId && !value.IsDeleted,
            cancellationToken)
            ?? throw new InvalidOperationException("لیست پیدا نشد.");
        if (await _db.InvoiceIssuanceBatchSalesLists.AnyAsync(
                value => value.SalesListId == salesListId,
                cancellationToken))
            throw new InvalidOperationException("این لیست قبلاً وارد فرایند صدور فاکتور شده است.");
        return list;
    }

    private async Task<string> GenerateOrderNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var number = $"ORD-{now:yyMMdd}-{Random.Shared.Next(1000, 10000)}";
            if (!await _db.Orders.AnyAsync(order => order.OrderNumber == number, cancellationToken))
                return number;
        }
        throw new InvalidOperationException("تولید شماره سفارش یکتا ناموفق بود.");
    }

    private static string CustomerKey(SalesListRequest request)
    {
        var username = NormalizeCustomerUsername(request.TelegramUsername);
        return !string.IsNullOrWhiteSpace(username)
            ? $"username:{username}"
            : $"telegram:{request.TelegramUserId.Trim()}";
    }

    private static string? NormalizeCustomerUsername(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().TrimStart('@').ToLowerInvariant();

    private static SalesListProductionCopy CreateProductionCopy(SalesList list)
    {
        var orderList = FormatOrderList(list);
        return new SalesListProductionCopy(
            list.Id,
            list.PublicCode,
            list.EnglishName,
            orderList,
            FormatLabelList(list));
    }

    private static string FormatOrderList(SalesList list)
    {
        var header = FormatOrderHeader(list);
        var roster = list.Requests
            .Where(request => request.Kind == SalesListRequestKind.CurrentBottle)
            .GroupBy(request => request.VolumeMl)
            .OrderByDescending(group => group.Key)
            .Select(group => $"{group.Key} ml:\n" + string.Join("\n", group
                .OrderBy(request => request.ConfirmedAt)
                .ThenBy(request => request.CreatedAt)
                .Select(ProductionIdentity)))
            .ToArray();
        return string.Join("\n\n", new[]
        {
            header,
            string.Join("\n\n", roster),
            FormatNextBottleSection(list)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatOrderHeader(SalesList list)
    {
        var gender = list.Gender switch
        {
            PerfumeGender.Women => "#women 👩",
            PerfumeGender.Men => "#men 👨",
            _ => "#unisex 👩‍🦰👨"
        };
        var brand = "#" + string.Concat(list.DisplayBrand.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_')).Trim('_');
        return $"کد: {list.PublicCode}\n" +
            $"{list.EnglishName}\n{brand}\n{gender}\nL.{list.ReleaseYear}\n\n" +
            $"{list.PersianName}\n\n" +
            $"🍊 نت‌های ابتدایی: {list.TopNotes}\n" +
            $"🌸 نت‌های میانی: {list.MiddleNotes}\n" +
            $"🌳 نت‌های پایانی: {list.BaseNotes}\n" +
            $"🎼 آکوردها: {list.Accords}\n\n" +
            $"حجم کل: {list.TotalVolume}ml\n" +
            $"قیمت هر میل: {list.PricePerMl:N0} تومان\n" +
            $"حداقل درخواست: {list.MinimumRequestVolumeMl} میل | باقی‌مانده: {list.RemainingVolume} میل";
    }

    private static string FormatNextBottleSection(SalesList list)
    {
        var next = list.Requests
            .Where(request => request.Kind == SalesListRequestKind.NextBottle)
            .OrderBy(request => request.CreatedAt)
            .Select(ProductionIdentity)
            .ToArray();
        return next.Length == 0
            ? "Next Bottle: اولین نفر صف باتل باشید 😘😘"
            : "Next Bottle: " + string.Join("، ", next);
    }

    private static string ProductionIdentity(SalesListRequest request)
    {
        var identity = string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}";
        if (request.IsGift)
        {
            var recipient = !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
                ? $"@{request.GiftRecipientTelegramUsername.TrimStart('@')}"
                : request.GiftRecipientTelegramUserId ?? "گیرنده نامشخص";
            identity += $" for {recipient}";
        }
        if (request.IsBottleOwner)
            identity += " 👑";
        if (request.Bottle?.Type == BottleType.Fancy)
            identity += " F";
        if (request.OmitIdentityOnLabel)
            identity += " B Id";
        else if (!string.IsNullOrWhiteSpace(request.LabelIdentityText))
            identity += $" {request.LabelIdentityText.Trim()}=L";
        return identity;
    }

    private static string FormatLabelList(SalesList list)
    {
        var owner = list.Requests.FirstOrDefault(request =>
            request.Kind == SalesListRequestKind.CurrentBottle && request.IsBottleOwner);
        var ownerGiftVolume = owner is null
            ? 0
            : list.Requests.Where(request =>
                    request.Kind == SalesListRequestKind.CurrentBottle &&
                    request.IsGift && IsGiftFor(request, owner))
                .Sum(request => request.VolumeMl);
        var labelRows = new List<(int Volume, DateTime SortAt, string Identity)>();
        foreach (var request in list.Requests
                     .Where(request => request.Kind == SalesListRequestKind.CurrentBottle)
                     .OrderBy(request => request.ConfirmedAt)
                     .ThenBy(request => request.CreatedAt))
        {
            if (request.IsGift && owner is not null && IsGiftFor(request, owner))
                continue;
            var volume = request.IsBottleOwner
                ? request.VolumeMl + ownerGiftVolume
                : request.VolumeMl;
            var identity = !string.IsNullOrWhiteSpace(request.LabelIdentityText)
                ? request.LabelIdentityText.Trim()
                : request.IsGift
                    ? GiftRecipientIdentity(request)
                    : BaseIdentity(request);
            if (request.IsBottleOwner)
                identity += " 👑";
            if (request.Bottle?.Type == BottleType.Fancy)
                identity += " F";
            if (request.OmitIdentityOnLabel && string.IsNullOrWhiteSpace(request.LabelIdentityText))
                identity += " B Id";
            labelRows.Add((volume, request.ConfirmedAt ?? request.CreatedAt, identity));
        }

        var roster = labelRows
            .GroupBy(row => row.Volume)
            .OrderByDescending(group => group.Key)
            .Select(group => $"{group.Key} ml:\n" + string.Join("\n", group
                .OrderBy(row => row.SortAt)
                .Select(row => row.Identity)))
            .ToArray();
        return FormatOrderHeader(list) + "\n\n" + string.Join("\n\n", roster) +
               "\n\n" + FormatNextBottleSection(list);
    }

    private static bool IsGiftFor(SalesListRequest gift, SalesListRequest recipient)
    {
        if (!string.IsNullOrWhiteSpace(gift.GiftRecipientTelegramUserId) &&
            string.Equals(gift.GiftRecipientTelegramUserId.Trim(), recipient.TelegramUserId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(gift.GiftRecipientTelegramUsername) &&
               !string.IsNullOrWhiteSpace(recipient.TelegramUsername) &&
               string.Equals(
                   gift.GiftRecipientTelegramUsername.Trim().TrimStart('@'),
                   recipient.TelegramUsername.Trim().TrimStart('@'),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string GiftRecipientIdentity(SalesListRequest request) =>
        !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
            ? $"@{request.GiftRecipientTelegramUsername.Trim().TrimStart('@')}"
            : !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUserId)
                ? $"کاربر {request.GiftRecipientTelegramUserId.Trim()}"
                : "گیرنده نامشخص";

    private static string BaseIdentity(SalesListRequest request) =>
        string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? $"کاربر {request.TelegramUserId}"
            : $"@{request.TelegramUsername.TrimStart('@')}";
}
