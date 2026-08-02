namespace ZibasheERP.API.Diagnostics;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString().Trim();
        var correlationId = IsValid(supplied)
            ? supplied
            : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private static bool IsValid(string value) =>
        value.Length is >= 8 and <= 100 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}
