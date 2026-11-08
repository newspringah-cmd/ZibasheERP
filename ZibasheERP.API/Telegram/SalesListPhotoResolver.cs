using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public static class SalesListPhotoResolver
{
    // An inventory reference must use the selected channel post's image.
    public static async Task<string?> ResolveSourcePostAsync(AppDbContext db, SalesList list, CancellationToken ct)
    {
        if(!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)) return list.TelegramPhotoFileId;
        var imported=await db.TelegramSalesListImports.AsNoTracking().Where(value=>!value.IsDeleted &&
            (value.SalesListId==list.Id || (list.TelegramMessageId!=null && value.SourceChannelId==list.TelegramChannelId && value.SourceMessageId==list.TelegramMessageId)) &&
            value.TelegramPhotoFileId!=null && value.TelegramPhotoFileId.Trim()!="")
            .OrderByDescending(value=>value.ReviewedAt??value.CreatedAt).Select(value=>value.TelegramPhotoFileId).FirstOrDefaultAsync(ct);
        if(!string.IsNullOrWhiteSpace(imported))return imported;
        if(!list.TelegramMessageId.HasValue || string.IsNullOrWhiteSpace(list.TelegramChannelId))return null;
        return await db.SalesLists.AsNoTracking().Where(value=>!value.IsDeleted&&!value.IsInventoryOffer&&
            value.TelegramChannelId==list.TelegramChannelId&&value.TelegramMessageId==list.TelegramMessageId&&
            value.TelegramPhotoFileId!=null&&value.TelegramPhotoFileId.Trim()!="")
            .OrderByDescending(value=>value.OpenDate).Select(value=>value.TelegramPhotoFileId).FirstOrDefaultAsync(ct);
    }

    public static async Task<string?> ResolveAsync(AppDbContext db, SalesList list, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)) return list.TelegramPhotoFileId;
        // Recover only from another cycle of the exact same variant, never a brand logo
        // or an arbitrary fragrance sharing the base perfume record.
        return await db.SalesLists.AsNoTracking()
            .Where(value => !value.IsDeleted && !value.IsInventoryOffer && value.PerfumeId == list.PerfumeId &&
                value.EnglishName == list.EnglishName && value.DisplayBrand == list.DisplayBrand &&
                value.ProductPageUrl == list.ProductPageUrl &&
                value.TelegramPhotoFileId != null && value.TelegramPhotoFileId.Trim() != "")
            .OrderByDescending(value => value.OpenDate).ThenByDescending(value => value.CreatedAt)
            .Select(value => value.TelegramPhotoFileId).FirstOrDefaultAsync(ct);
    }
}
