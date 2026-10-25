using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public sealed record AdminItemActivitySummary(long UserId, string? Username, string DisplayName,
    int Registered, int Removed, int VolumeChanged)
{
    public int Total => Registered + Removed + VolumeChanged;
}

public static class AdminItemActivityReport
{
    // ConfirmedAt survives cancellation: this is registration activity, not current stock.
    public static IQueryable<SalesListRequest> CustomerRegistrations(IQueryable<SalesListRequest> requests,
        DateTime since, DateTime until) => requests.Where(request => !request.CreatedByAdmin &&
            request.ConfirmedAt.HasValue && request.ConfirmedAt >= since && request.ConfirmedAt < until &&
            !request.ExternalReference.StartsWith("auto-next:") && !request.SalesList.IsInventoryOffer);

    public static bool CanView(long userId, long chatId, string chatType, string ownerId) =>
        userId > 0 && long.TryParse(ownerId, out var owner) && owner == userId && chatId == userId &&
        string.Equals(chatType, "private", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedPeriod(int days) => days is 7 or 15 or 30;

    public static IReadOnlyList<AdminItemActivitySummary> Summarize(IEnumerable<AdminItemActivity> events, DateTime since, DateTime until) =>
        events.Where(row => !row.IsDeleted && row.ActorTelegramUserId > 0 && row.Action != AdminItemAction.TrackingStarted &&
                row.CreatedAt >= since && row.CreatedAt < until)
            .GroupBy(row => row.ActorTelegramUserId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).First();
                return new AdminItemActivitySummary(group.Key, latest.ActorUsername, latest.ActorDisplayName,
                    group.Count(row => row.Action == AdminItemAction.Registered),
                    group.Count(row => row.Action == AdminItemAction.Removed),
                    group.Count(row => row.Action == AdminItemAction.VolumeChanged));
            })
            .OrderByDescending(row => row.Total).ThenBy(row => row.UserId).ToArray();
}
