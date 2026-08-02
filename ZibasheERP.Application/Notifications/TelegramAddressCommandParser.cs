namespace ZibasheERP.Application.Notifications;

public sealed record TelegramAddressCommand(
    string Description,
    string ReceiverName,
    string Province,
    string City,
    string PostalCode,
    string FullAddress);

public static class TelegramAddressCommandParser
{
    public static TelegramAddressCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        var command = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
        if (!string.Equals(
                command.Split('@', 2)[0],
                "/addaddress",
                StringComparison.OrdinalIgnoreCase) ||
            firstSpace < 0)
        {
            return null;
        }

        var fields = trimmed[(firstSpace + 1)..]
            .Split('|')
            .Select(value => value.Trim())
            .ToArray();
        if (fields.Length != 6 || fields.Any(string.IsNullOrWhiteSpace))
            return null;

        return new(
            fields[0],
            fields[1],
            fields[2],
            fields[3],
            fields[4],
            fields[5]);
    }
}
