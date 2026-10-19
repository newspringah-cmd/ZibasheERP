using System.Linq.Expressions;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public static class CustomerListReservationPolicy
{
    public const string DuplicateMessage = "شما یک ایتم در لیست دارید در صورت درخواست مجدد ایتم دیگر از همین لیست کامنت بگذارید";

    public static bool RequiresSingleItem(SalesListRequest request) => !request.CreatedByAdmin && !request.IsGift;

    public static Expression<Func<SalesListRequest, bool>> ExistingItem(Guid listId, string userId, string? username, Guid? excluding = null)
    {
        var normalized = username?.Trim().TrimStart('@').ToLowerInvariant();
        return request => request.SalesListId == listId && request.Id != excluding && !request.IsDeleted &&
            request.Kind == SalesListRequestKind.CurrentBottle && !request.IsGift &&
            (request.Status == SalesListRequestStatus.Confirmed || request.Status == SalesListRequestStatus.Promoted ||
             request.Status == SalesListRequestStatus.QueuedForInvoice || request.Status == SalesListRequestStatus.Invoiced) &&
            (request.TelegramUserId == userId || (normalized != null && normalized != "" &&
                request.TelegramUsername != null && request.TelegramUsername.Trim().Replace("@", "").ToLower() == normalized));
    }
}
