namespace ZibasheERP.Application.Notifications;

public enum TelegramCallbackType
{
    Unknown = 0,
    SelectSalesList = 1,
    SelectVolume = 2,
    SelectBottle = 3,
    ConfirmOrder = 4,
    Cancel = 5,
    ViewOrder = 6,
    StartPayment = 7,
    ChooseDeliveryAddress = 8,
    SetDeliveryAddress = 9,
    MenuLists = 10,
    MenuOrders = 11,
    MenuBalance = 12,
    MenuAddresses = 13,
    TrackOrder = 14,
    ViewInvoice = 15
}

public sealed record TelegramCallback(
    TelegramCallbackType Type,
    Guid SalesListId,
    int? VolumeMl = null,
    Guid? BottleId = null);

public static class TelegramCallbackParser
{
    public static TelegramCallback Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return new(TelegramCallbackType.Unknown, Guid.Empty);

        if (data == "cancel")
            return new(TelegramCallbackType.Cancel, Guid.Empty);

        var menuType = data switch
        {
            "menu:lists" => TelegramCallbackType.MenuLists,
            "menu:orders" => TelegramCallbackType.MenuOrders,
            "menu:balance" => TelegramCallbackType.MenuBalance,
            "menu:addresses" => TelegramCallbackType.MenuAddresses,
            _ => TelegramCallbackType.Unknown
        };
        if (menuType != TelegramCallbackType.Unknown)
            return new(menuType, Guid.Empty);

        var parts = data.Split(':');
        if (parts.Length < 2 || !TryDecodeGuid(parts[1], out var salesListId))
            return new(TelegramCallbackType.Unknown, Guid.Empty);

        if (parts[0] == "cancel" && parts.Length == 2)
            return new(TelegramCallbackType.Cancel, salesListId);

        if (parts[0] == "list" && parts.Length == 2)
            return new(TelegramCallbackType.SelectSalesList, salesListId);

        if (parts[0] == "volume" &&
            parts.Length == 3 &&
            int.TryParse(parts[2], out var volume) &&
            volume > 0)
        {
            return new(TelegramCallbackType.SelectVolume, salesListId, volume);
        }

        if (parts[0] == "confirm" && parts.Length == 2)
            return new(TelegramCallbackType.ConfirmOrder, salesListId);

        if (parts[0] == "order" && parts.Length == 2)
            return new(TelegramCallbackType.ViewOrder, salesListId);

        if (parts[0] == "pay" && parts.Length == 2)
            return new(TelegramCallbackType.StartPayment, salesListId);

        if (parts[0] == "track" && parts.Length == 2)
            return new(TelegramCallbackType.TrackOrder, salesListId);

        if (parts[0] == "invoice" && parts.Length == 2)
            return new(TelegramCallbackType.ViewInvoice, salesListId);

        if (parts[0] == "shipaddr" && parts.Length == 2)
            return new(TelegramCallbackType.ChooseDeliveryAddress, salesListId);

        if (parts[0] == "setaddr" &&
            parts.Length == 3 &&
            TryDecodeGuid(parts[2], out var addressId))
        {
            return new(
                TelegramCallbackType.SetDeliveryAddress,
                salesListId,
                BottleId: addressId);
        }

        if (parts[0] == "b" &&
            parts.Length == 4 &&
            int.TryParse(parts[2], out volume) &&
            volume > 0 &&
            TryDecodeGuid(parts[3], out var bottleId))
        {
            return new(
                TelegramCallbackType.SelectBottle,
                salesListId,
                volume,
                bottleId);
        }

        return new(TelegramCallbackType.Unknown, Guid.Empty);
    }

    public static string EncodeGuid(Guid value) =>
        Convert.ToBase64String(value.ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecodeGuid(string value, out Guid result)
    {
        if (Guid.TryParseExact(value, "N", out result))
            return true;

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 16)
            {
                result = new Guid(bytes);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        result = Guid.Empty;
        return false;
    }
}
