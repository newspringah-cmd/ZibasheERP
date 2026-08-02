namespace ZibasheERP.Application.Notifications;

public enum TelegramCallbackType
{
    Unknown = 0,
    SelectSalesList = 1,
    SelectVolume = 2
}

public sealed record TelegramCallback(
    TelegramCallbackType Type,
    Guid SalesListId,
    int? VolumeMl = null);

public static class TelegramCallbackParser
{
    public static TelegramCallback Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return new(TelegramCallbackType.Unknown, Guid.Empty);

        var parts = data.Split(':');
        if (parts.Length < 2 || !Guid.TryParseExact(parts[1], "N", out var salesListId))
            return new(TelegramCallbackType.Unknown, Guid.Empty);

        if (parts[0] == "list" && parts.Length == 2)
            return new(TelegramCallbackType.SelectSalesList, salesListId);

        if (parts[0] == "volume" &&
            parts.Length == 3 &&
            int.TryParse(parts[2], out var volume) &&
            volume > 0)
        {
            return new(TelegramCallbackType.SelectVolume, salesListId, volume);
        }

        return new(TelegramCallbackType.Unknown, Guid.Empty);
    }
}
