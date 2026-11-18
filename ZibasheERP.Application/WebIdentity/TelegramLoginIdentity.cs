using System.Globalization;

namespace ZibasheERP.Application.WebIdentity;

public enum TelegramLoginIdentifierKind { Username, NumericId }
public sealed record TelegramLoginIdentifier(TelegramLoginIdentifierKind Kind, string Value)
{
    public static TelegramLoginIdentifier? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 65) return null;
        var value = new string(input.Trim().Select(c => c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') :
            c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c).ToArray());
        if (value.All(char.IsAsciiDigit))
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? new(TelegramLoginIdentifierKind.NumericId, id.ToString(CultureInfo.InvariantCulture)) : null;
        var username = value.StartsWith('@') ? value[1..] : value;
        return username.Length is >= 1 and <= 32 && char.IsAsciiLetter(username[0]) &&
            username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? new(TelegramLoginIdentifierKind.Username, username.ToLowerInvariant()) : null;
    }
}

// Trusted directory data only, never a client-provided identity or a phone extracted from a postal address.
public sealed record TelegramLoginBinding(Guid CustomerId, long TelegramUserId, string? CurrentUsername, bool IsActive);
public interface IVerifiedTelegramLoginDirectory
{
    Task<IReadOnlyList<TelegramLoginBinding>> FindAsync(TelegramLoginIdentifier identifier, CancellationToken ct);
}

public static class TelegramLoginTarget
{
    // Resolves a delivery destination ONLY. This does not authenticate, issue a proof/session or grant access.
    // Future challenge delivery must address the stable TelegramUserId in private, never the typed username/group.
    public static TelegramLoginBinding? Resolve(TelegramLoginIdentifier identifier, IReadOnlyList<TelegramLoginBinding> matches)
    {
        if (matches.Count != 1) return null;
        var match = matches[0];
        if (!match.IsActive || match.CustomerId == Guid.Empty || match.TelegramUserId <= 0) return null;
        var agrees = identifier.Kind == TelegramLoginIdentifierKind.NumericId
            ? match.TelegramUserId.ToString(CultureInfo.InvariantCulture) == identifier.Value
            : TelegramLoginIdentifier.Parse(match.CurrentUsername) == identifier;
        return agrees ? match : null;
    }
}
