using MediatR;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Invoices.IssueInvoice;
using ZibasheERP.Application.Interfaces;
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
                var bottleAmount = request.IsBottleOwner ? 0 : request.BottlePrice;
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
        }
    }

    public async Task<InvoiceIssuanceResult> IssueManualAsync(
        string customerIdentity,
        IReadOnlyCollection<ManualInvoiceLineInput> lines,
        string productPhotoFileId,
        string issuedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        var identity = customerIdentity.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidOperationException("شناسه مشتری لازم است.");
        var validLines = lines.Where(line => !string.IsNullOrWhiteSpace(line.Description) &&
                                             line.Quantity > 0 && line.UnitAmount >= 0 && line.BottleAmount >= 0).ToArray();
        if (validLines.Length == 0)
            throw new InvalidOperationException("حداقل یک ردیف معتبر برای فاکتور دستی لازم است.");

        var usernameWithAt = $"@{identity}";
        var customer = await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            (value.TelegramId == identity || value.Username == identity || value.Username == usernameWithAt), cancellationToken)
            ?? throw new InvalidOperationException("مشتری پیدا نشد؛ ابتدا مشتری را با Telegram ID یا @username شناسایی کنید.");
        var now = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(), CreatedAt = now, CustomerId = customer.Id,
            OrderNumber = await GenerateOrderNumberAsync(now, cancellationToken),
            Status = OrderStatus.Registered, RegisteredAt = now, Source = OrderSource.ManualInvoice,
            Notes = $"فاکتور دستی توسط {issuedByTelegramUserId.Trim()}"
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
            new IssueInvoiceCommand(order.Id, productPhotoFileId.Trim()),
            cancellationToken);
        return new InvoiceIssuanceResult(Guid.Empty, 1, new[] { invoice.InvoiceNumber }, Array.Empty<SalesListProductionCopy>());
    }

    public async Task<InvoicePaymentTrackingReport?> GetPaymentTrackingReportAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.InvoiceIssuanceBatches.AsNoTracking()
            .Include(value => value.SalesLists)
                .ThenInclude(value => value.SalesList)
            .FirstOrDefaultAsync(value => value.Id == batchId && !value.IsDeleted, cancellationToken);
        if (batch is null)
            return null;

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
        var listCodes = batch.SalesLists.Select(value => value.SalesList.PublicCode)
            .OrderBy(value => value).ToArray();
        var rows = orders.Select((order, index) =>
        {
            invoices.TryGetValue(order.Id, out var invoice);
            var identity = !string.IsNullOrWhiteSpace(order.Customer?.Username)
                ? $"@{order.Customer.Username.TrimStart('@')}"
                : order.Customer?.TelegramId ?? order.Customer?.FullName ?? "مشتری نامشخص";
            var paid = invoice?.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Paid &&
                       order.Status == OrderStatus.Paid;
            return $"{(paid ? "✅" : "🔴")} {index + 1}. {identity} — " +
                   $"{invoice?.InvoiceNumber ?? "بدون فاکتور"} — {order.FinalAmount:N0} تومان";
        });
        var message = $"💳 واریز جدید\nلیست‌ها: {string.Join("، ", listCodes)}\n" +
                      $"تعداد فاکتور: {orders.Length}\n\n{string.Join("\n", rows)}\n\n" +
                      $"✅ پرداخت‌شده   🔴 در انتظار پرداخت\nآخرین بروزرسانی: {DateTime.UtcNow.AddHours(3.5):yyyy/MM/dd HH:mm}";
        var actions = orders.Where(order =>
                invoices.TryGetValue(order.Id, out var invoice) &&
                invoice.Status != ZibasheERP.Domain.Enums.InvoiceStatus.Paid &&
                order.Status != OrderStatus.Paid)
            .SelectMany(order => order.Items.Select(item => new InvoicePaymentTrackingAction(
                item.Id,
                $"📤 {(order.Customer?.Username ?? order.Customer?.TelegramId ?? "مشتری").TrimStart('@')} | " +
                $"{item.SalesList?.EnglishName ?? item.ManualDescription ?? "آیتم"} | {item.RequestedVolumeMl}ml")))
            .ToArray();
        return new InvoicePaymentTrackingReport(
            batch.Id, message, batch.TelegramPaymentTrackingChatId,
            batch.TelegramPaymentTrackingMessageId, actions);
    }

    public async Task SetPaymentTrackingMessageAsync(
        Guid batchId,
        string chatId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        var batch = await _db.InvoiceIssuanceBatches.FirstOrDefaultAsync(
            value => value.Id == batchId && !value.IsDeleted,
            cancellationToken) ?? throw new InvalidOperationException("نوبت صدور فاکتور پیدا نشد.");
        batch.TelegramPaymentTrackingChatId = chatId.Trim();
        batch.TelegramPaymentTrackingMessageId = messageId;
        batch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Customer> ResolveCustomerAsync(SalesListRequest request, CancellationToken cancellationToken)
    {
        var telegramId = request.TelegramUserId.Trim();
        var username = request.TelegramUsername?.Trim().TrimStart('@');
        var usernameWithAt = string.IsNullOrWhiteSpace(username) ? null : $"@{username}";
        var customer = await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            (value.TelegramId == telegramId || (!string.IsNullOrWhiteSpace(username) &&
                                                 (value.Username == username || value.Username == usernameWithAt))), cancellationToken);
        if (customer is not null) return customer;

        customer = new Customer
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            TelegramId = telegramId, Username = username,
            FullName = string.IsNullOrWhiteSpace(username) ? $"مشتری تلگرام {telegramId}" : $"@{username}",
            Mobile = $"TG-{telegramId}"[..Math.Min(20, telegramId.Length + 3)],
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

    private static string CustomerKey(SalesListRequest request) => request.TelegramUserId.Trim();

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
            var identity = request.IsGift
                ? GiftRecipientIdentity(request)
                : BaseIdentity(request);
            if (request.IsBottleOwner)
                identity += " 👑";
            if (request.Bottle?.Type == BottleType.Fancy)
                identity += " F";
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
