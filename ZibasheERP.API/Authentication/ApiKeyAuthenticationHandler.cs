using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ZibasheERP.API.Authentication;

public sealed class ApiKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration _configuration;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(
                ApiKeyAuthenticationDefaults.HeaderName,
                out var suppliedValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var suppliedKey = suppliedValues.ToString();
        var telegramKey = _configuration["ApiKeys:TelegramBot"];
        var adminKey = _configuration["ApiKeys:Admin"];
        var n8nKey = _configuration["ApiKeys:N8n"];

        var role = Matches(suppliedKey, adminKey)
            ? "Admin"
            : Matches(suppliedKey, telegramKey)
                ? "TelegramBot"
                : Matches(suppliedKey, n8nKey)
                    ? "N8n"
                    : null;

        if (role is null)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("API key is invalid or not configured."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, role),
            new Claim(ClaimTypes.Role, role)
        };
        var identity = new ClaimsIdentity(
            claims,
            ApiKeyAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(
            principal,
            ApiKeyAuthenticationDefaults.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool Matches(string suppliedKey, string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey) ||
            string.IsNullOrWhiteSpace(configuredKey))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        return suppliedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
