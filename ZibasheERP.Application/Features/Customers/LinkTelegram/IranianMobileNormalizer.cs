namespace ZibasheERP.Application.Features.Customers.LinkTelegram;

public static class IranianMobileNormalizer
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new string(value
            .Where(char.IsDigit)
            .Select(ToLatinDigit)
            .ToArray());

        if (digits.StartsWith("0098", StringComparison.Ordinal))
            digits = "0" + digits[4..];
        else if (digits.StartsWith("98", StringComparison.Ordinal) && digits.Length == 12)
            digits = "0" + digits[2..];

        return digits.Length == 11 && digits.StartsWith("09", StringComparison.Ordinal)
            ? digits
            : null;
    }

    private static char ToLatinDigit(char value) => value switch
    {
        >= '\u06F0' and <= '\u06F9' => (char)('0' + value - '\u06F0'),
        >= '\u0660' and <= '\u0669' => (char)('0' + value - '\u0660'),
        _ => value
    };
}
