using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed class InvoiceInventoryService : IInvoiceInventoryService
{
    private readonly AppDbContext _db;

    public InvoiceInventoryService(AppDbContext db) => _db = db;

    public async Task<InvoiceInventoryPreview> GetPreviewAsync(
        Guid orderItemId, CancellationToken cancellationToken = default)
    {
        var item = await LoadItemAsync(orderItemId, false, cancellationToken);
        Validate(item);
        return new InvoiceInventoryPreview(
            item.Id, CustomerIdentity(item.Order!.Customer!),
            item.SalesList!.EnglishName, item.RequestedVolumeMl,
            item.Bottle?.Name ?? "بدون شیشه", item.BottlePrice, item.LineTotal);
    }

    public async Task<InvoiceInventoryReleaseResult> ReleaseAsync(
        Guid orderItemId, decimal newTotalAmount, long adminTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var existingOffer = await _db.SalesLists.AsNoTracking()
            .Include(value => value.FixedBottle)
            .Include(value => value.Perfume)
            .FirstOrDefaultAsync(value => value.SourceOrderItemId == orderItemId && !value.IsDeleted, cancellationToken);
        if (existingOffer is not null)
        {
            var sourceItem = await _db.OrderItems.AsNoTracking()
                .Include(value => value.Order)
                .FirstAsync(value => value.Id == orderItemId, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new InvoiceInventoryReleaseResult(
                existingOffer.Id, existingOffer.PublicCode, existingOffer.EnglishName,
                existingOffer.TotalVolume, existingOffer.FixedBottle?.Name ?? "بدون شیشه",
                existingOffer.PricePerMl * existingOffer.TotalVolume + (existingOffer.FixedBottlePrice ?? 0),
                existingOffer.TelegramPhotoFileId ?? string.Empty,
                sourceItem.Order!.InvoiceIssuanceBatchId!.Value);
        }
        var item = await LoadItemAsync(orderItemId, true, cancellationToken);
        Validate(item);
        if (newTotalAmount < item.BottlePrice)
            throw new InvalidOperationException($"مبلغ کل نمی‌تواند از هزینه شیشه ({item.BottlePrice:N0} تومان) کمتر باشد.");
        if (await _db.SalesLists.AnyAsync(value => value.SourceOrderItemId == item.Id && !value.IsDeleted, cancellationToken))
            throw new InvalidOperationException("این آیتم قبلاً به موجودی منتقل شده است.");

        var order = item.Order!;
        var invoice = orderInvoice(order);
        var request = await FindSourceRequestAsync(item, cancellationToken);
        var now = DateTime.UtcNow;
        var oldAmount = item.LineTotal;
        item.IsDeleted = true;
        item.UpdatedAt = now;
        request.Status = SalesListRequestStatus.Cancelled;
        request.UpdatedAt = now;

        foreach (var payment in order.Payments.Where(value =>
                     !value.IsDeleted && value.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.UpdatedAt = now;
            payment.Notes = $"لغو خودکار پس از انتقال آیتم {item.Id:N} به موجودی";
        }

        var remaining = order.Items.Where(value => value.Id != item.Id && !value.IsDeleted).ToArray();
        order.PerfumeTotal = remaining.Sum(value => value.PerfumeAmount);
        order.BottleTotal = remaining.Sum(value => value.BottlePrice);
        order.FinalAmount = remaining.Sum(value => value.LineTotal);
        order.UpdatedAt = now;
        invoice.PerfumeTotal = order.PerfumeTotal;
        invoice.BottleTotal = order.BottleTotal;
        invoice.TotalAmount = order.FinalAmount;
        invoice.UpdatedAt = now;
        if (remaining.Length == 0)
        {
            order.Status = OrderStatus.Cancelled;
            order.CancelledAt = now;
            order.CancelReason = $"تمام آیتم‌ها به موجودی منتقل شد؛ مدیر تلگرام {adminTelegramUserId}";
            invoice.Status = InvoiceStatus.Cancelled;
        }
        order.Customer!.CurrentDebt = Math.Max(0, order.Customer.CurrentDebt - oldAmount);
        order.Customer.UpdatedAt = now;

        var source = item.SalesList!;
        var perfumeAmount = newTotalAmount - item.BottlePrice;
        var offer = new SalesList
        {
            Id = Guid.NewGuid(), CreatedAt = now,
            PublicCode = await NextPublicCodeAsync(cancellationToken),
            EnglishName = source.EnglishName, PersianName = source.PersianName,
            ProductPageUrl = source.ProductPageUrl, DisplayBrand = source.DisplayBrand,
            Gender = source.Gender, ReleaseYear = source.ReleaseYear,
            TopNotes = source.TopNotes, MiddleNotes = source.MiddleNotes,
            BaseNotes = source.BaseNotes, Accords = source.Accords,
            PerfumeId = source.PerfumeId,
            PricePerMl = decimal.Round(perfumeAmount / item.RequestedVolumeMl, 2),
            TotalVolume = item.RequestedVolumeMl,
            MinimumRequestVolumeMl = item.RequestedVolumeMl,
            ReservedVolume = 0, OpenDate = now, Status = SalesListStatus.Open,
            TelegramPhotoFileId = source.TelegramPhotoFileId,
            IsInventoryOffer = true, SourceOrderItemId = item.Id,
            FixedBottleId = item.BottleId,
            FixedBottlePrice = item.BottlePrice,
            Notes = $"موجودی برگشتی از فاکتور {invoice.InvoiceNumber}؛ مدیر {adminTelegramUserId}"
        };
        await _db.SalesLists.AddAsync(offer, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new InvoiceInventoryReleaseResult(
            offer.Id, offer.PublicCode, offer.EnglishName, offer.TotalVolume,
            item.Bottle?.Name ?? "بدون شیشه", newTotalAmount,
            offer.TelegramPhotoFileId ?? string.Empty,
            order.InvoiceIssuanceBatchId!.Value);

        static Invoice orderInvoice(Order order) => order.InvoicesSingle();
    }

    private async Task<OrderItem> LoadItemAsync(Guid id, bool tracking, CancellationToken ct)
    {
        IQueryable<OrderItem> query = _db.OrderItems
            .Include(value => value.Bottle)
            .Include(value => value.SalesList).ThenInclude(value => value!.Perfume)
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.Order).ThenInclude(value => value!.Payments)
            .Include(value => value.Order).ThenInclude(value => value!.Invoices);
        query = query.Include(value => value.Order).ThenInclude(value => value!.Items);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(value => value.Id == id && !value.IsDeleted, ct)
               ?? throw new InvalidOperationException("آیتم فاکتور پیدا نشد.");
    }

    private static void Validate(OrderItem item)
    {
        var order = item.Order ?? throw new InvalidOperationException("سفارش پیدا نشد.");
        var invoice = order.InvoicesSingle();
        if (order.InvoiceIssuanceBatchId is null || item.SalesList is null || item.PerfumeId is null)
            throw new InvalidOperationException("فقط آیتم‌های فاکتور لیست فروش قابل انتقال به موجودی هستند.");
        if (order.Status is OrderStatus.Paid or OrderStatus.Cancelled || invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Cancelled)
            throw new InvalidOperationException("فاکتور پرداخت‌شده یا لغوشده قابل انتقال به موجودی نیست.");
        if (order.Payments.Any(value => !value.IsDeleted && value.Status == PaymentStatus.Confirmed))
            throw new InvalidOperationException("برای این فاکتور پرداخت تأییدشده وجود دارد؛ ابتدا فرایند استرداد را انجام دهید.");
        if (string.IsNullOrWhiteSpace(item.SalesList.TelegramPhotoFileId))
            throw new InvalidOperationException("عکس عطر برای انتشار موجودی ثبت نشده است.");
        if (!item.BottleId.HasValue)
            throw new InvalidOperationException("این آیتم شیشه مشخصی ندارد و قابل انتقال خودکار نیست.");
    }

    private async Task<SalesListRequest> FindSourceRequestAsync(OrderItem item, CancellationToken ct)
    {
        if (item.SourceSalesListRequestId.HasValue)
            return await _db.SalesListRequests.FirstOrDefaultAsync(value =>
                       value.Id == item.SourceSalesListRequestId && !value.IsDeleted, ct)
                   ?? throw new InvalidOperationException("درخواست اولیه این آیتم پیدا نشد.");
        var customer = item.Order!.Customer!;
        var username = customer.Username?.TrimStart('@');
        var matches = await _db.SalesListRequests.Where(value =>
                value.SalesListId == item.SalesListId && !value.IsDeleted &&
                value.Status == SalesListRequestStatus.Invoiced &&
                value.VolumeMl == item.RequestedVolumeMl && value.BottleId == item.BottleId &&
                (value.TelegramUserId == customer.TelegramId ||
                 (username != null && value.TelegramUsername == username)))
            .ToArrayAsync(ct);
        if (matches.Length != 1)
            throw new InvalidOperationException("درخواست اولیه این آیتم به‌صورت یکتا شناسایی نشد؛ حذف خودکار متوقف شد.");
        return matches[0];
    }

    private async Task<int> NextPublicCodeAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var code = Random.Shared.Next(10000, 100000);
            if (!await _db.SalesLists.AnyAsync(value => value.PublicCode == code && !value.IsDeleted, ct))
                return code;
        }
        throw new InvalidOperationException("ساخت کد یکتای موجودی ناموفق بود.");
    }

    private static string CustomerIdentity(Customer customer) =>
        !string.IsNullOrWhiteSpace(customer.Username) ? $"@{customer.Username.TrimStart('@')}" :
        customer.TelegramId ?? customer.FullName;
}

file static class InvoiceInventoryOrderExtensions
{
    public static Invoice InvoicesSingle(this Order order) =>
        order.Invoices.SingleOrDefault(value => !value.IsDeleted)
        ?? throw new InvalidOperationException("فاکتور سفارش پیدا نشد.");
}
