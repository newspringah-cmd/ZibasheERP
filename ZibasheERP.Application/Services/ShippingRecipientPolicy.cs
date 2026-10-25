using System.Linq.Expressions;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public static class ShippingRecipientPolicy
{
    public static Expression<Func<OrderItem, bool>> ForCustomer(Guid customerId, string? telegramId, string? username)
    {
        var id = telegramId?.Trim() ?? "";
        var name = username?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
        return item => !item.IsDeleted && item.Order != null && !item.Order.IsDeleted &&
            item.Order.Status != OrderStatus.Cancelled &&
            (item.SourceSalesListRequest == null || !item.SourceSalesListRequest.IsGift
                ? item.Order.CustomerId == customerId
                : !item.SourceSalesListRequest.IsDeleted &&
                  ((id != "" && item.SourceSalesListRequest.GiftRecipientTelegramUserId != null &&
                      item.SourceSalesListRequest.GiftRecipientTelegramUserId.Trim() == id) ||
                   (name != "" && item.SourceSalesListRequest.GiftRecipientTelegramUsername != null &&
                      item.SourceSalesListRequest.GiftRecipientTelegramUsername.Trim().Replace("@", "").ToLower() == name)));
    }
}
