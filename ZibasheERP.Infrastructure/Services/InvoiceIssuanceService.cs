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

    public async Task<InvoiceIssuanceResult> IssueManualAsync(
        string customerIdentity,
        IReadOnlyCollection<ManualInvoiceLineInput> lines,
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
        var invoice = await _sender.Send(new IssueInvoiceCommand(order.Id), cancellationToken);
        return new InvoiceIssuanceResult(Guid.Empty, 1, new[] { invoice.InvoiceNumber }, Array.Empty<SalesListProductionCopy>());
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
            var number = $"ORD-{now:yyyyMMddHHmmss}-{Random.Shared.Next(1000, 10000)}";
            if (!await _db.Orders.AnyAsync(order => order.OrderNumber == number, cancellationToken))
                return number;
        }
        throw new InvalidOperationException("تولید شماره سفارش یکتا ناموفق بود.");
    }

    private static string CustomerKey(SalesListRequest request) => request.TelegramUserId.Trim();

    private static SalesListProductionCopy CreateProductionCopy(SalesList list)
    {
        var rows = list.Requests
            .OrderBy(request => request.ConfirmedAt)
            .ThenBy(request => request.CreatedAt)
            .Select((request, index) =>
            {
                var identity = !string.IsNullOrWhiteSpace(request.TelegramUsername)
                    ? $"@{request.TelegramUsername.TrimStart('@')}"
                    : request.TelegramUserId;
                var bottle = request.IsBottleOwner
                    ? "صاحب باتل — شیشه رایگان"
                    : request.Bottle is null
                        ? "شیشه ثبت نشده"
                        : request.Bottle.Type == BottleType.Fancy
                            ? $"شیشه فانتزی F — {request.Bottle.Name}"
                            : $"شیشه نرمال — {request.Bottle.Name}";
                return $"{index + 1}. {identity} — {request.VolumeMl} میل — {bottle}";
            })
            .ToArray();
        var header = $"کد لیست: {list.PublicCode}\nعطر: {list.EnglishName}\n" +
            $"حجم کل: {list.TotalVolume} میل\nتعداد آیتم: {rows.Length}";
        return new SalesListProductionCopy(
            list.Id,
            list.PublicCode,
            list.EnglishName,
            "🧪 نسخه دکانت — آماده پس از صدور فاکتور\n" + header + "\n\n" + string.Join("\n", rows),
            "🏷 نسخه چاپ لیبل\n" + header + "\n\n" + string.Join("\n", rows));
    }
}
