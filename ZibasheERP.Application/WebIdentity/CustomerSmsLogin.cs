using System.Security.Cryptography;
using System.Text;

namespace ZibasheERP.Application.WebIdentity;

// Directory adapters must use an explicitly verified binding, never Customer.Mobile/address text alone.
public sealed record SmsLoginCustomer(Guid CustomerId, string VerifiedMobile, bool IsActive);
public interface IVerifiedSmsCustomerDirectory
{
    Task<IReadOnlyList<SmsLoginCustomer>> FindByVerifiedMobileAsync(string mobile, CancellationToken ct);
    Task<SmsLoginCustomer?> GetAsync(Guid customerId, CancellationToken ct);
}

public interface ICustomerLoginSmsSender
{
    Task SendLoginCodeAsync(string mobile, string code, CancellationToken ct);
}

public enum SmsLoginStartStatus { Accepted, InvalidMobile, Throttled }
public sealed record SmsLoginStart(SmsLoginStartStatus Status, string? ChallengeId, int RetryAfterSeconds);
public sealed record VerifiedSmsCustomer(Guid CustomerId, DateTimeOffset VerifiedAt);

public static class IranianLoginMobile
{
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 40) return null;
        var normalized = new StringBuilder();
        foreach (var c in input.Trim())
        {
            if (c is >= '۰' and <= '۹') normalized.Append((char)('0' + c - '۰'));
            else if (c is >= '٠' and <= '٩') normalized.Append((char)('0' + c - '٠'));
            else if (char.IsAsciiDigit(c) || c == '+') normalized.Append(c);
            else if (c is not (' ' or '-' or '(' or ')')) return null;
        }
        var value = normalized.ToString();
        if (value.StartsWith("+98", StringComparison.Ordinal)) value = "0" + value[3..];
        else if (value.StartsWith("0098", StringComparison.Ordinal)) value = "0" + value[4..];
        return value.Length == 11 && value.StartsWith("09", StringComparison.Ordinal) && value.All(char.IsAsciiDigit)
            ? value : null;
    }
}

/// <summary>
/// Isolated staging implementation: single-process, bounded in-memory challenges only.
/// NOT registered in production. A shared atomic store and edge limits are prerequisites for real rollout.
/// Caller supplies a server-derived client key, never a browser-provided identity or forwarded IP without trust checks.
/// </summary>
public sealed class StagingCustomerSmsLogin(
    IVerifiedSmsCustomerDirectory directory, ICustomerLoginSmsSender sender, TimeProvider clock) : IDisposable
{
    private sealed record Challenge(Guid? CustomerId, string Mobile, byte[] Digest, DateTimeOffset ExpiresAt)
    {
        public int Attempts { get; set; }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _verifications = new(StringComparer.Ordinal);
    private const int MaximumEntries = 2000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    public async Task<SmsLoginStart> BeginAsync(string? input, string clientKey, CancellationToken ct = default)
    {
        var mobile = IranianLoginMobile.Normalize(input);
        if (mobile is null) return new(SmsLoginStartStatus.InvalidMobile, null, 0);
        ValidateClientKey(clientKey);
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            Prune(now);
            var mobileKey = Key("mobile:" + mobile);
            var client = Key("client:" + clientKey);
            if (_challenges.Count >= MaximumEntries || _requests.Count + 2 > MaximumEntries ||
                !CanRequest(mobileKey, now, 3, TimeSpan.FromSeconds(60)) ||
                !CanRequest(client, now, 10, TimeSpan.Zero))
                return new(SmsLoginStartStatus.Throttled, null, 60);
            Track(_requests, mobileKey, now);
            Track(_requests, client, now);
            // A resend invalidates every older code for this mobile, including after a failed delivery.
            foreach (var key in _challenges.Where(c => c.Value.Mobile == mobile).Select(c => c.Key).ToArray())
                _challenges.Remove(key);

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            Guid? customerId = null;
            try
            {
                var matches = await directory.FindByVerifiedMobileAsync(mobile, ct);
                if (matches.Count == 1 && matches[0] is { IsActive: true } customer &&
                    customer.CustomerId != Guid.Empty && customer.VerifiedMobile == mobile)
                {
                    await sender.SendLoginCodeAsync(mobile, code, ct);
                    customerId = customer.CustomerId;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // No provider message/credential/code escapes to the caller. Production adapter must emit redacted metrics.
            }
            _challenges[id] = new(customerId, mobile, Digest(id, code), now.Add(Lifetime));
            // Same shape for unknown, blocked, ambiguous, unbound and delivery-failed accounts.
            return new(SmsLoginStartStatus.Accepted, id, 60);
        }
        finally { _gate.Release(); }
    }

    public async Task<VerifiedSmsCustomer?> VerifyAsync(string? id, string? input, string clientKey, CancellationToken ct = default)
    {
        ValidateClientKey(clientKey);
        if (id is null || id.Length != 64 || !id.All(char.IsAsciiHexDigit)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            Prune(now);
            var client = Key("verify:" + clientKey);
            if (_verifications.Count >= MaximumEntries ||
                (_verifications.TryGetValue(client, out var times) && times.Count >= 30)) return null;
            Track(_verifications, client, now);
            if (!_challenges.TryGetValue(id, out var challenge)) return null;
            challenge.Attempts++;
            if (challenge.Attempts >= 5) _challenges.Remove(id);
            var code = NormalizeCode(input);
            if (code is null || !CryptographicOperations.FixedTimeEquals(challenge.Digest, Digest(id, code))) return null;
            _challenges.Remove(id); // atomic consume before any await: concurrent/replayed verification cannot succeed.
            if (challenge.CustomerId is not Guid customerId) return null;
            SmsLoginCustomer? current;
            try { current = await directory.GetAsync(customerId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
            if (current is not { IsActive: true } || current.CustomerId != customerId || current.VerifiedMobile != challenge.Mobile)
                return null;
            return new(customerId, now); // Customer proof only; not a cookie, bearer token, or staff privilege.
        }
        finally { _gate.Release(); }
    }

    private static string? NormalizeCode(string? input)
    {
        if (input is null || input.Length != 6) return null;
        var value = new string(input.Select(c => c is >= '۰' and <= '۹' ? (char)('0' + c - '۰') :
            c is >= '٠' and <= '٩' ? (char)('0' + c - '٠') : c).ToArray());
        return value.All(char.IsAsciiDigit) ? value : null;
    }

    private static void ValidateClientKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("A server-derived client key is required.", nameof(key));
    }

    private string Key(string value) => Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(value)));
    private byte[] Digest(string id, string code) => HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(id + ":" + code));
    private bool CanRequest(string key, DateTimeOffset now, int limit, TimeSpan cooldown) =>
        !_requests.TryGetValue(key, out var times) || (times.Count < limit && now - times[^1] >= cooldown);
    private static void Track(Dictionary<string, List<DateTimeOffset>> entries, string key, DateTimeOffset now)
    {
        if (!entries.TryGetValue(key, out var times)) entries[key] = times = [];
        times.Add(now);
    }
    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _challenges.Where(c => c.Value.ExpiresAt <= now).Select(c => c.Key).ToArray()) _challenges.Remove(key);
        foreach (var entries in new[] { _requests, _verifications })
            foreach (var key in entries.Keys.ToArray())
            {
                entries[key].RemoveAll(t => t <= now - Window);
                if (entries[key].Count == 0) entries.Remove(key);
            }
    }
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_hmacKey);
        _challenges.Clear();
        _requests.Clear();
        _verifications.Clear();
        _gate.Dispose();
    }
}
