using System.Reflection;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Features.Invoices;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class FlashSaleTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var start = DateTime.UtcNow;
        var offer = new FlashSalePromotion { StartsAtUtc = start, EndsAtUtc = start.AddHours(2),
            OriginalPricePerMl = 10001, DiscountPercent = 50 };
        check(!offer.IsActiveAt(start.AddTicks(-1)) && offer.IsActiveAt(start) &&
            !offer.IsActiveAt(offer.EndsAtUtc), "flash sale uses an inclusive start and exclusive end");
        check(offer.DiscountedPricePerMl == 5000.50m, "perfume-only offer retains decimal price precision");
        check(typeof(FlashSalesController).GetCustomAttribute<AuthorizeAttribute>()?.Roles == "Admin",
            "flash-sale activation and publication receipts require Admin authorization");

        var connection = Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var target = new SqlConnectionStringBuilder(connection);
        if (target.InitialCatalog != "ZibasheERPCloudDev" || target.DataSource != "127.0.0.1,14333")
            throw new InvalidOperationException("Flash-sale tests require the isolated development database");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        await using var db = new AppDbContext(options);
        var perfumeId = await db.Perfumes.Select(value => value.Id).FirstAsync();
        var prefix = "flashfixture_" + Guid.NewGuid().ToString("N");
        var bottle = new Bottle { Id = Guid.NewGuid(), Name = prefix, VolumeMl = 5, SalePrice = 2000,
            Type = BottleType.Normal, IsActive = true, CreatedAt = start };
        SalesList List(decimal price) => new() { Id = Guid.NewGuid(), PerfumeId = perfumeId,
            PublicCode = Random.Shared.Next(3000000, 4000000), EnglishName = prefix, PersianName = prefix,
            PricePerMl = price, TotalVolume = 1000, Status = SalesListStatus.Open, OpenDate = start, CreatedAt = start,
            TelegramPhotoFileId = "fixture-photo-no-telegram-calls", TelegramChannelId = "-1001236168661",
            TelegramMessageId = Random.Shared.Next(10000000, 20000000) };
        var list = List(20000); var other = List(30000); var ids = new List<Guid> { list.Id, other.Id };
        db.Bottles.Add(bottle); db.SalesLists.AddRange(list, other); await db.SaveChangesAsync();
        var requests = new List<Guid>();
        async Task<SalesListRequest> Pending(SalesList selected, decimal price, Guid? promotionId = null, bool admin = false)
        {
            db.ChangeTracker.Clear();
            var id = Guid.NewGuid(); requests.Add(id);
            var request = new SalesListRequest { Id = id, SalesListId = selected.Id, VolumeMl = 5,
                TelegramUserId = prefix + requests.Count, TelegramUsername = prefix + requests.Count,
                ExternalReference = prefix + id.ToString("N"), PerfumePricePerMl = price, FlashSalePromotionId = promotionId,
                CreatedByAdmin = admin,
                BottleId = bottle.Id, BottlePrice = bottle.SalePrice, ExpiresAt = DateTime.UtcNow.AddMinutes(10), CreatedAt = start };
            db.SalesListRequests.Add(request); await db.SaveChangesAsync(); return request;
        }
        async Task Confirm(SalesListRequest request)
        {
            db.ChangeTracker.Clear();
            await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(request.Id, request.TelegramUserId);
        }
        try
        {
            var before = await Pending(list, list.PricePerMl); await Confirm(before);
            var promotionId = Guid.NewGuid();
            var controller = new FlashSalesController(db, Microsoft.Extensions.Options.Options.Create(new ZibasheERP.API.Telegram.TelegramOptions { OwnerUserId="771234567" }));
            var activation = new FlashSalesController.CreateRequest(promotionId, list.Id, ActorUserId:771234567);
            check(await controller.Create(activation, default) is OkObjectResult,
                "eligible offer activates without changing the ordinary list price");
            check(await controller.Create(activation, default) is OkObjectResult &&
                await db.FlashSalePromotions.CountAsync(value => value.SalesListId == list.Id) == 1,
                "repeated activation reuses the same durable campaign");
            check(await controller.Create(activation with { RequestId = Guid.NewGuid() }, default) is ConflictObjectResult,
                "overlapping campaigns on the same list are rejected");
            var quote = await FlashSalePricing.QuoteAsync(db, list, DateTime.UtcNow);
            check(quote.PricePerMl == 10000 && quote.PromotionId == promotionId &&
                (await FlashSalePricing.QuoteAsync(db, other, DateTime.UtcNow)).PricePerMl == 30000,
                "only the advertised list receives the fifty-percent perfume quote");
            var during = await Pending(list, quote.PricePerMl, quote.PromotionId); await Confirm(during);
            // Manual admin entry initially carries the ordinary list price; the common
            // final confirmation must still assign the active campaign and its price.
            var adminDuring = await Pending(list, 20000, admin: true); await Confirm(adminDuring);
            var adminSaved = await db.SalesListRequests.AsNoTracking().SingleAsync(value => value.Id == adminDuring.Id);
            check(adminSaved.CreatedByAdmin && adminSaved.FlashSalePromotionId == promotionId &&
                adminSaved.PerfumePricePerMl == 10000 && adminSaved.BottlePrice == 2000,
                "manual admin entry receives the active perfume discount at final confirmation without discounting its bottle");
            var cycle = List(60000);
            cycle.StablePublicCode = list.DisplayCode;
            cycle.OpenDate = DateTime.UtcNow;
            cycle.CreatedAt = cycle.OpenDate;
            ids.Add(cycle.Id);
            db.SalesLists.Add(cycle); await db.SaveChangesAsync();
            var inherited = await FlashSalePricing.QuoteAsync(db, cycle, DateTime.UtcNow);
            check(inherited.PromotionId == promotionId && inherited.PricePerMl == 10000,
                "a new bottle cycle keeps the original fifty-percent campaign price and deadline");
            check(await controller.Create(activation with { RequestId = Guid.NewGuid(), SalesListId = cycle.Id }, default) is ConflictObjectResult,
                "a rollover cannot restart an overlapping two-hour offer");
            var offerView = (OkObjectResult)await controller.Get(promotionId, default);
            using (var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(offerView.Value)))
                check(json.RootElement.GetProperty("CurrentSalesListId").GetGuid() == cycle.Id &&
                    json.RootElement.GetProperty("SalesListId").GetGuid() == list.Id,
                    "campaign status follows the current channel list without replacing its original invoice reference");
            var cycleCustomer = await Pending(cycle, 60000); await Confirm(cycleCustomer);
            var cycleAdmin = await Pending(cycle, 60000, admin: true); await Confirm(cycleAdmin);
            var futureCycle = List(60000); futureCycle.StablePublicCode = list.DisplayCode;
            futureCycle.OpenDate = (await db.FlashSalePromotions.AsNoTracking().SingleAsync(value => value.Id == promotionId)).EndsAtUtc;
            check((await FlashSalePricing.QuoteAsync(db, futureCycle, DateTime.UtcNow)).PromotionId == null,
                "a cycle opened at or after the original deadline does not inherit the offer");
            var otherRequest = await Pending(other, 30000, admin: true); await Confirm(otherRequest);
            var expiredPending = await Pending(list, 10000, promotionId, admin: true);
            await db.FlashSalePromotions.Where(value => value.Id == promotionId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.EndsAtUtc, DateTime.UtcNow));
            var expiredRejected = false;
            try { await Confirm(expiredPending); }
            catch (InvalidOperationException error) when (error.Message.Contains("مهلت تخفیف")) { expiredRejected = true; }
            check(expiredRejected, "a selection started during the offer cannot confirm the discounted price after expiry");
            var after = await Pending(list, 20000, admin: true); await Confirm(after);
            db.ChangeTracker.Clear();
            var saved = await db.SalesListRequests.AsNoTracking().Where(value => requests.Contains(value.Id)).ToArrayAsync();
            check(saved.Single(value => value.Id == during.Id).PerfumePricePerMl == 10000 &&
                saved.Single(value => value.Id == during.Id).FlashSalePromotionId == promotionId &&
                saved.Where(value => value.Status == SalesListRequestStatus.Confirmed && value.Id != during.Id && value.Id != adminDuring.Id &&
                    value.Id != cycleCustomer.Id && value.Id != cycleAdmin.Id)
                    .All(value => value.FlashSalePromotionId == null),
                "confirmation snapshots identify exactly the qualifying item, excluding before, after and other perfumes");
            var editBlocked = false;
            try { await new SalesListRequestRepository(db).UpdateConfirmedCurrentBottleRequestAsync(during.Id, 5, 6, bottle.Id, 2000); }
            catch (InvalidOperationException error) when (error.Message.Contains("بعد از پایان تخفیف")) { editBlocked = true; }
            check(editBlocked, "expired discounted items cannot be enlarged retrospectively");
            db.ChangeTracker.Clear();
            await db.SalesLists.Where(value => ids.Contains(value.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, SalesListStatus.Full)
                    .SetProperty(value => value.PricePerMl, 40000m));
            var service = new InvoiceIssuanceService(db, DispatchProxy.Create<ISender, FlashInvoiceSender>());
            var preview = await service.PreviewCompletedListsAsync(ids);
            check(preview.InvoiceCount == 7 && preview.TotalAmount == 814000m,
                "invoice preview after expiry preserves the qualifying discount and the full bottle charges");
            await service.IssueCompletedListsAsync(ids, "isolated-fixture");
            var items = await db.OrderItems.AsNoTracking().Where(value => value.SalesListId.HasValue && ids.Contains(value.SalesListId.Value)).ToArrayAsync();
            var discounted = items.Single(value => value.SourceSalesListRequestId == during.Id);
            check(discounted.PerfumePricePerMl == 10000 && discounted.PerfumeAmount == 50000 &&
                discounted.BottlePrice == 2000 && discounted.LineTotal == 52000,
                "actual invoice issuance after expiry keeps fifty percent off perfume and no reduction on its bottle");
            var adminInvoiceItem = items.Single(value => value.SourceSalesListRequestId == adminDuring.Id);
            check(adminInvoiceItem.PerfumePricePerMl == 10000 && adminInvoiceItem.PerfumeAmount == 50000 &&
                adminInvoiceItem.BottlePrice == 2000 && adminInvoiceItem.LineTotal == 52000,
                "admin-entered items retain the same promotional price in an invoice issued after expiry");
            check(items.Where(value => value.SalesListId == cycle.Id).All(value => value.PerfumePricePerMl == 10000 &&
                    value.PerfumeAmount == 50000 && value.BottlePrice == 2000) &&
                items.Count(value => value.SalesListId == cycle.Id) == 2,
                "customer and admin reservations on the next bottle cycle retain their offer in later invoices");
            check(items.Where(value => value.SourceSalesListRequestId != during.Id && value.SourceSalesListRequestId != adminDuring.Id)
                    .Where(value => value.SalesListId != cycle.Id)
                    .All(value => value.PerfumePricePerMl == 40000) &&
                items.Sum(value => value.LineTotal) == preview.TotalAmount,
                "ordinary items follow the final list price while the promotional invoice snapshot stays unchanged");
        }
        finally
        {
            db.ChangeTracker.Clear();
            var orderIds = await db.OrderItems.Where(value => value.SalesListId.HasValue && ids.Contains(value.SalesListId.Value)).Select(value => value.OrderId).ToArrayAsync();
            var batchIds = await db.InvoiceIssuanceBatchSalesLists.Where(value => ids.Contains(value.SalesListId)).Select(value => value.InvoiceIssuanceBatchId).ToArrayAsync();
            await db.OrderItems.Where(value => orderIds.Contains(value.OrderId)).ExecuteDeleteAsync();
            await db.Orders.Where(value => orderIds.Contains(value.Id)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatchSalesLists.Where(value => ids.Contains(value.SalesListId)).ExecuteDeleteAsync();
            await db.InvoiceIssuanceBatches.Where(value => batchIds.Contains(value.Id)).ExecuteDeleteAsync();
            await db.Customers.Where(value => value.TelegramId != null && value.TelegramId.StartsWith(prefix)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(value => ids.Contains(value.SalesListId)).ExecuteDeleteAsync();
            await db.FlashSalePromotions.Where(value => ids.Contains(value.SalesListId)).ExecuteDeleteAsync();
            await db.SalesLists.Where(value => ids.Contains(value.Id)).ExecuteDeleteAsync();
            await db.Bottles.Where(value => value.Id == bottle.Id).ExecuteDeleteAsync();
        }
    }
}

public class FlashInvoiceSender : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Task.FromResult(new InvoiceResponse(Guid.NewGuid(), Guid.Empty, "fixture-invoice", "Issued", "Pending",
            DateTime.UtcNow, 0, 0, 0, null!, []));
}
