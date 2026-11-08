using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Marketing;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/flash-sales")]
[Authorize(Roles = "Admin")]
public sealed class FlashSalesController(AppDbContext db, IOptions<TelegramOptions> options) : ControllerBase
{
    public const long PromotionGroupChatId = -1001283018740;
    public sealed record CreateRequest(Guid RequestId, Guid SalesListId, int DiscountPercent = 50, int DurationMinutes = 120,
        decimal? DiscountAmountPerMl = null, long ActorUserId = 0, decimal? ExpectedOriginalPricePerMl = null);
    public sealed record MessageRequest(long MessageId);
    public sealed record EndRequest(long ActorUserId);
    private bool IsManager(long id) => long.TryParse(options.Value.OwnerUserId, out var owner) && owner > 0 && id == owner;

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken ct)
    {
        if (!IsManager(request.ActorUserId)) return Forbid();
        if (request.RequestId == Guid.Empty || request.SalesListId == Guid.Empty ||
            request.DurationMinutes is < 1 or > 10080 || (request.DiscountAmountPerMl.HasValue
                ? request.DiscountPercent != 0 || request.DiscountAmountPerMl <= 0 || decimal.Round(request.DiscountAmountPerMl.Value, 2) != request.DiscountAmountPerMl
                : request.DiscountPercent is < 1 or > 99)) return BadRequest();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize activation with both other campaigns and customer confirmations.
        var list = await db.SalesLists.FromSqlInterpolated($"SELECT * FROM [SalesLists] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={request.SalesListId}")
            .SingleOrDefaultAsync(ct);
        if (list is null || list.IsDeleted) return NotFound();
        var existing = await db.FlashSalePromotions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == request.RequestId, ct);
        if (existing is not null)
            return existing.SalesListId == request.SalesListId && existing.DiscountPercent == request.DiscountPercent &&
                existing.DiscountAmountPerMl == request.DiscountAmountPerMl && existing.RequestedDurationMinutes == request.DurationMinutes
                ? Ok(View(existing, list)) : Conflict();
        var now = DateTime.UtcNow;
        if (list.Status != SalesListStatus.Open || (list.IsInventoryOffer && !list.InventoryCode.HasValue) || list.RemainingVolume <= 0 || list.PricePerMl <= 0 ||
            string.IsNullOrWhiteSpace(list.TelegramPhotoFileId) || !list.TelegramMessageId.HasValue ||
            (list.IsInventoryOffer ? list.TelegramChannelId != options.Value.ReadyInventoryChannelId : list.TelegramChannelId != "-1001236168661")) return Conflict("لیست برای تبلیغ تخفیف‌دار آماده نیست.");
        if (request.ExpectedOriginalPricePerMl.HasValue && request.ExpectedOriginalPricePerMl != list.PricePerMl)
            return Conflict("قیمت عطر تغییر کرده است؛ پیش‌نمایش باید دوباره تأیید شود.");
        if (request.DiscountAmountPerMl >= list.PricePerMl) return BadRequest("مبلغ تخفیف باید کمتر از قیمت هر میل باشد.");
        if (await (from value in db.FlashSalePromotions
                join origin in db.SalesLists on value.SalesListId equals origin.Id
                where !value.IsDeleted && value.CancelledAtUtc == null && value.EndsAtUtc > now &&
                    (value.SalesListId == list.Id || (!list.IsInventoryOffer && !origin.IsInventoryOffer && origin.PerfumeId == list.PerfumeId &&
                     (origin.StablePublicCode ?? origin.PublicCode) == list.DisplayCode))
                select value.Id).AnyAsync(ct)) return Conflict("تخفیف دیگری برای این لیست فعال است.");
        var promotion = new FlashSalePromotion { Id = request.RequestId, SalesListId = list.Id,
            CreatedAt = now, StartsAtUtc = now, EndsAtUtc = now.AddMinutes(request.DurationMinutes),
            OriginalPricePerMl = list.PricePerMl, DiscountPercent = request.DiscountPercent,
            DiscountAmountPerMl = request.DiscountAmountPerMl, RequestedDurationMinutes = request.DurationMinutes, GroupChatId = PromotionGroupChatId };
        if (promotion.DiscountedPricePerMl <= 0 || promotion.DiscountedPricePerMl >= promotion.OriginalPricePerMl)
            return BadRequest("قیمت تخفیف‌دار باید مثبت و کمتر از قیمت عادی باشد.");
        db.FlashSalePromotions.Add(promotion);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Ok(View(promotion, list));
    }

    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, EndRequest request, CancellationToken ct)
    {
        if (!IsManager(request.ActorUserId)) return Forbid();
        var promotion = await ZibasheERP.Infrastructure.Services.FlashSaleLifecycle.EndAsync(db, id, ct);
        if (promotion is null) return NotFound();
        var origin = await db.SalesLists.AsNoTracking().SingleAsync(value => value.Id == promotion.SalesListId, ct);
        return Ok(View(promotion, origin));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var promotion = await db.FlashSalePromotions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id && !value.IsDeleted, ct);
        if (promotion is null) return NotFound();
        var origin = await db.SalesLists.AsNoTracking().SingleAsync(value => value.Id == promotion.SalesListId, ct);
        // The original post URL is transferred to the latest bottle cycle.
        var list = origin.IsInventoryOffer ? origin : await db.SalesLists.AsNoTracking().Where(value => !value.IsDeleted && !value.IsInventoryOffer &&
                value.PerfumeId == origin.PerfumeId && (value.StablePublicCode ?? value.PublicCode) == origin.DisplayCode &&
                value.OpenDate >= origin.OpenDate && value.TelegramChannelId == "-1001236168661" && value.TelegramMessageId != null)
            .OrderByDescending(value => value.OpenDate).FirstOrDefaultAsync(ct) ?? origin;
        return Ok(View(promotion, list));
    }

    [HttpPost("{id:guid}/message")]
    public async Task<IActionResult> Message(Guid id, MessageRequest request, CancellationToken ct)
    {
        if (request.MessageId <= 0) return BadRequest();
        // Binding is idempotent and cannot replace another publication receipt.
        var changed = await db.FlashSalePromotions.Where(value => value.Id == id && !value.IsDeleted &&
                (value.TelegramMessageId == null || value.TelegramMessageId == request.MessageId))
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.TelegramMessageId, request.MessageId)
                .SetProperty(value => value.UpdatedAt, DateTime.UtcNow), ct);
        return changed == 1 ? NoContent() : Conflict();
    }

    private static object View(FlashSalePromotion promotion, SalesList list) => new
    {
        promotion.Id, promotion.SalesListId, CurrentSalesListId = list.Id, Code = list.DisplayCode, list.EnglishName, list.DisplayBrand,
        promotion.StartsAtUtc, promotion.EndsAtUtc, promotion.DiscountPercent, promotion.OriginalPricePerMl,
        promotion.DiscountAmountPerMl, promotion.RequestedDurationMinutes,
        DiscountedPricePerMl = promotion.DiscountedPricePerMl, promotion.GroupChatId, promotion.TelegramMessageId,
        list.TelegramPhotoFileId, list.IsInventoryOffer, list.InventoryCode, ListChatId = list.TelegramChannelId,
        ListUrl = list.IsInventoryOffer ? InventoryOffersController.PostUrl(list.TelegramChannelId,list.TelegramMessageId) : $"https://t.me/zibasheperfume/{list.TelegramMessageId}",
        list.RemainingVolume, IsActive = promotion.IsActiveAt(DateTime.UtcNow) && list.Status == SalesListStatus.Open && list.RemainingVolume > 0,
        Caption = HourlyPromotionCopy.Caption(promotion, list, DateTime.UtcNow), ThankYouText = HourlyPromotionCopy.EndMessage(promotion, list)
    };
}
