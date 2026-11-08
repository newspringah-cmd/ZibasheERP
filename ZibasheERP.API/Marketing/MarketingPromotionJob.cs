using System.Security.Cryptography;
using System.Text;

namespace ZibasheERP.API.Marketing;

public sealed record MarketingPromotionJob(Guid SalesListId, int Code, string Destination,
    long AdminChatId, long AdminUserId, int HoldHours = 24, string? ResultUrl = null, string ChannelMode = "replace", string? MessageText = null,
    HourlyPromotionSettings? FlashSale = null, Guid? InvoiceBatchId = null, string? CustomText = null)
{
    public const string Channel = "MarketingPromotion";
    public const string RemoveChannel = "channel-delete";
    public const string GroupMessage = "group-message";
    public const string FlashStart = "flash-start";
    public const string FlashEnd = "flash-end";
    public static Guid GroupMessageRequestId(long chatId, long userId, long messageId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"admin-group-message:{chatId}:{userId}:{messageId}"))[..16]);
    public static Guid RemovalRequestId(long chatId, long userId, string callbackId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"marketing-remove:{chatId}:{userId}:{callbackId}"))[..16]);
    public static bool IsDestination(string value) => value is "group" or "channel";
    public static Guid RequestId(long chatId, long userId, long messageId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"marketing:{chatId}:{userId}:{messageId}"))[..16]);
}

public sealed record HourlyPromotionSettings(Guid CampaignId, int DurationMinutes = 120, int DiscountPercent = 0,
    decimal? DiscountAmountPerMl = null, decimal OriginalPricePerMl = 0);
