using System.Text.Json;

namespace ZibasheERP.Application.Notifications;

public static class N8nDeliveryTargetValidator
{
    public static bool HasApprovedTelegramGroup(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("Delivery", out var delivery) &&
                   delivery.ValueKind == JsonValueKind.Object &&
                   delivery.TryGetProperty("Channel", out var channel) &&
                   string.Equals(channel.GetString(), "TelegramGroup", StringComparison.Ordinal) &&
                   delivery.TryGetProperty("ChatId", out var target) &&
                   !string.IsNullOrWhiteSpace(target.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool MatchesTelegramGroup(string payload, string chatId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("Delivery", out var delivery) ||
                delivery.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return delivery.TryGetProperty("Channel", out var channel) &&
                   string.Equals(channel.GetString(), "TelegramGroup", StringComparison.Ordinal) &&
                   delivery.TryGetProperty("ChatId", out var target) &&
                   string.Equals(target.GetString(), chatId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
