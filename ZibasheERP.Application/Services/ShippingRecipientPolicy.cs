using System.Linq.Expressions;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public static class ShippingRecipientPolicy
{
    public const string ManualGiftMarker = " | هدیه برای ";

    public static IQueryable<OrderItem> ItemsForCustomer(IQueryable<OrderItem> items,
        IQueryable<NotificationOutbox> notifications, Guid customerId, string? telegramId, string? username,
        IQueryable<Customer> profiles)
    {
        // Manual gifts have no SalesListRequest. Their durable recipient delivery
        // events carry the actual CustomerId, independently of the paying order.
        var gifts = notifications.Where(n => !n.IsDeleted && n.OrderId != null && n.Channel == "Telegram" &&
            (n.EventType == "GiftInvoiceIssued" || n.EventType == "InvoiceGiftDeliveryRequiresManualAction"));
        var regularIds = items.Where(ForCustomer(customerId, telegramId, username))
            .Where(i => i.Order!.Source != OrderSource.ManualInvoice ||
                ((i.Order.Notes == null || !i.Order.Notes.Contains(ManualGiftMarker)) && !gifts.Any(n => n.OrderId == i.OrderId)))
            .Select(i => i.Id);
        var owned = items.Where(i => regularIds.Contains(i.Id) ||
            (!i.IsDeleted && i.Order != null && !i.Order.IsDeleted && i.Order.Status != OrderStatus.Cancelled &&
             i.Order.Source == OrderSource.ManualInvoice &&
             gifts.Any(n => n.OrderId == i.OrderId && n.CustomerId == customerId) &&
             !gifts.Any(n => n.OrderId == i.OrderId && n.CustomerId != customerId)));
        var parameter = Expression.Parameter(typeof(OrderItem), "item");
        Expression<Func<OrderItem, bool>> activeProfile = i => profiles.Any(c => c.Id == customerId && !c.IsDeleted);
        Expression<Func<OrderItem, bool>> isGift = i => i.SourceSalesListRequest != null && i.SourceSalesListRequest.IsGift;
        var source = Expression.Property(parameter, nameof(OrderItem.SourceSalesListRequest));
        var guard = Expression.AndAlso(Bind(activeProfile, parameter),
            Expression.OrElse(Expression.Not(Bind(isGift, parameter)), Bind(UniqueGiftRecipient(profiles, customerId), source)));
        return owned.Where(Expression.Lambda<Func<OrderItem, bool>>(guard, parameter));
    }

    public static IQueryable<SalesListRequest> GiftRequestsForCustomer(IQueryable<SalesListRequest> requests,
        IQueryable<Customer> profiles, Guid customerId, string? telegramId, string? username) =>
        requests.Where(r => r.IsGift && profiles.Any(c => c.Id == customerId && !c.IsDeleted))
            .Where(GiftMatches(telegramId, username)).Where(UniqueGiftRecipient(profiles, customerId));

    private static Expression<Func<SalesListRequest, bool>> UniqueGiftRecipient(IQueryable<Customer> profiles, Guid customerId) =>
        r => !profiles.Any(c => !c.IsDeleted && c.Id != customerId &&
            ((r.GiftRecipientTelegramUserId != null && c.TelegramId != null && r.GiftRecipientTelegramUserId.Trim() != "" &&
              c.TelegramId.Trim() == r.GiftRecipientTelegramUserId.Trim()) ||
             (r.GiftRecipientTelegramUsername != null && c.Username != null && r.GiftRecipientTelegramUsername.Trim().Replace("@", "") != "" &&
              c.Username.Trim().Replace("@", "").ToLower() == r.GiftRecipientTelegramUsername.Trim().Replace("@", "").ToLower())));

    private static Expression<Func<SalesListRequest, bool>> GiftMatches(string? telegramId, string? username)
    {
        var id = telegramId?.Trim() ?? "";
        var name = username?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
        return r => !r.IsDeleted &&
            ((r.GiftRecipientTelegramUserId != null && r.GiftRecipientTelegramUserId.Trim() != "" &&
              r.GiftRecipientTelegramUserId.Trim().Replace("0", "").Replace("1", "").Replace("2", "").Replace("3", "").Replace("4", "").Replace("5", "").Replace("6", "").Replace("7", "").Replace("8", "").Replace("9", "") == "")
             ? (id != "" && r.GiftRecipientTelegramUserId.Trim() == id)
             : ((id != "" && r.GiftRecipientTelegramUserId != null && r.GiftRecipientTelegramUserId.Trim() == id) ||
                (name != "" && r.GiftRecipientTelegramUsername != null && r.GiftRecipientTelegramUsername.Trim().Replace("@", "").ToLower() == name)));
    }

    public static Expression<Func<OrderItem, bool>> ForCustomer(Guid customerId, string? telegramId, string? username)
    {
        Expression<Func<OrderItem, bool>> valid = item => !item.IsDeleted && item.Order != null && !item.Order.IsDeleted &&
            item.Order.Status != OrderStatus.Cancelled &&
            (item.Order.Source != OrderSource.ManualInvoice || item.Order.Notes == null || !item.Order.Notes.Contains(ManualGiftMarker));
        Expression<Func<OrderItem, bool>> isGift = item => item.SourceSalesListRequest != null && item.SourceSalesListRequest.IsGift;
        Expression<Func<OrderItem, bool>> own = item => item.Order!.CustomerId == customerId;
        var parameter = valid.Parameters[0];
        var source = Expression.Property(parameter, nameof(OrderItem.SourceSalesListRequest));
        return Expression.Lambda<Func<OrderItem, bool>>(Expression.AndAlso(valid.Body,
            Expression.Condition(Bind(isGift, parameter), Bind(GiftMatches(telegramId, username), source), Bind(own, parameter))), parameter);
    }

    // Inline the same predicate into both entity queries; no compiled delegates
    // or expression invocations reach the SQL provider.
    private static Expression Bind(LambdaExpression source, Expression target) =>
        new ParameterBinder(source.Parameters[0], target).Visit(source.Body)!;
    private sealed class ParameterBinder(ParameterExpression parameter, Expression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? target : base.VisitParameter(node);
    }
}
