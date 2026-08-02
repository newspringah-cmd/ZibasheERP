namespace ZibasheERP.Application.Notifications;

public enum TelegramCommand
{
    Unknown = 0,
    Start = 1,
    Orders = 2,
    Lists = 3,
    Addresses = 4,
    Help = 5,
    Cancel = 6
}

public static class TelegramCommandParser
{
    public static TelegramCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TelegramCommand.Unknown;

        var firstToken = text.Trim().Split(' ', 2)[0];
        var command = firstToken.Split('@', 2)[0].ToLowerInvariant();

        return command switch
        {
            "/start" => TelegramCommand.Start,
            "/orders" => TelegramCommand.Orders,
            "/lists" => TelegramCommand.Lists,
            "/addresses" => TelegramCommand.Addresses,
            "/help" => TelegramCommand.Help,
            "/cancel" => TelegramCommand.Cancel,
            _ => TelegramCommand.Unknown
        };
    }
}
