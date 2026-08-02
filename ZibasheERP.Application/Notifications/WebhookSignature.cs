using System.Security.Cryptography;
using System.Text;

namespace ZibasheERP.Application.Notifications;

public static class WebhookSignature
{
    public static string Create(string secret, string timestamp, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);
        ArgumentNullException.ThrowIfNull(body);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(hash);
    }
}
