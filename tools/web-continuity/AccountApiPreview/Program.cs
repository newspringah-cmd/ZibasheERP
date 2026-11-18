using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Caching.Memory;
using ZibasheERP.Application.WebAccounts;
using ZibasheERP.Application.WebIdentity;

var port = args.Length == 0 ? 0 : int.Parse(args.Single());
using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder();
names.AddIpAddress(IPAddress.Loopback);
certificateRequest.CertificateExtensions.Add(names.Build());
using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Preview" });
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 4096;
    options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate));
});

var app = builder.Build();
var data = new SyntheticAccounts();
var reader = new CustomerInvoiceReader(data);
using var login = new StagingCustomerSmsLogin(data, data, TimeProvider.System);
using var sessions = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
const string cookieName = "__Host-ZibashePreview";
var lifetime = TimeSpan.FromMinutes(15);

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var authority = $"127.0.0.1:{context.Connection.LocalPort}";
    if (context.Request.Host.Value != authority)
    {
        context.Response.StatusCode = 400;
        return;
    }
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.Origin.ToString() != $"https://{authority}")
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next(context);
});

app.MapPost("/api/v2/session/sms/start", async (StartRequest request, HttpContext context, CancellationToken ct) =>
{
    var result = await login.BeginAsync(request.Mobile, ClientKey(context), ct);
    if (result.Status == SmsLoginStartStatus.InvalidMobile) return Results.BadRequest();
    if (result.Status == SmsLoginStartStatus.Throttled)
    {
        context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
        return Results.StatusCode(429);
    }
    return Results.Json(new { result.ChallengeId, result.RetryAfterSeconds }, statusCode: 202);
});

app.MapPost("/api/v2/session/sms/verify", async (VerifyRequest request, HttpContext context, CancellationToken ct) =>
{
    var proof = await login.VerifyAsync(request.ChallengeId, request.Code, ClientKey(context), ct);
    if (proof is null) return Results.Unauthorized();
    Revoke(context);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    sessions.Set(SessionKey(token), proof.CustomerId, new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = lifetime,
        Size = 1
    });
    if (!sessions.TryGetValue(SessionKey(token), out _)) return Results.StatusCode(503);
    context.Response.Cookies.Append(cookieName, token, CookieSettings(lifetime));
    return Results.NoContent();
});

app.MapPost("/api/v2/session/logout", (HttpContext context) =>
{
    Revoke(context);
    context.Response.Cookies.Delete(cookieName, CookieSettings());
    return Results.NoContent();
});

app.MapGet("/api/v2/me/invoices", async (HttpContext context, int? page, int? pageSize, CancellationToken ct) =>
{
    var token = ReadCookie(context);
    if (token is null || !sessions.TryGetValue<Guid>(SessionKey(token), out var customerId))
        return Results.Unauthorized();
    var customer = await data.GetAsync(customerId, ct);
    if (customer is not { IsActive: true }) return Results.Unauthorized();
    var actor = new CurrentWebActor(customerId, customerId, WebRole.Customer, WebAuthenticationStrength.CustomerSms, true);
    try
    {
        return Results.Ok(await reader.ReadMyInvoicesAsync(actor, page ?? 1, pageSize ?? 20, ct));
    }
    catch (ArgumentOutOfRangeException) { return Results.BadRequest(); }
    catch (InvalidOperationException) { return Results.Problem(statusCode: 503, title: "Invoice data unavailable"); }
});

await app.StartAsync();
Console.WriteLine($"PREVIEW_URL {app.Urls.Single()}");
await app.WaitForShutdownAsync();

void Revoke(HttpContext context)
{
    if (ReadCookie(context) is { } token) sessions.Remove(SessionKey(token));
}
string? ReadCookie(HttpContext context) =>
    context.Request.Cookies.TryGetValue(cookieName, out var token) && token.Length == 64 && token.All(char.IsAsciiHexDigit)
        ? token : null;
static string SessionKey(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress!.ToString();
static CookieOptions CookieSettings(TimeSpan? maxAge = null) => new()
{
    Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = maxAge
};

sealed record StartRequest(string? Mobile);
sealed record VerifyRequest(string? ChallengeId, string? Code);
