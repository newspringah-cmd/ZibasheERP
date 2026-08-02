using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramUpdateDeduplicationFilter : IAsyncActionFilter
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private readonly ITelegramUpdateDeduplicator _deduplicator;
    private readonly TelegramOptions _options;

    public TelegramUpdateDeduplicationFilter(
        ITelegramUpdateDeduplicator deduplicator,
        IOptions<TelegramOptions> options)
    {
        _deduplicator = deduplicator;
        _options = options.Value;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (!_options.Enabled ||
            !context.HttpContext.Request.Headers.TryGetValue(SecretHeader, out var supplied) ||
            !SecretsMatch(supplied.ToString(), _options.WebhookSecret) ||
            !context.ActionArguments.TryGetValue("update", out var value) ||
            value is not TelegramUpdate update)
        {
            await next();
            return;
        }

        if (!_deduplicator.TryAcquire(update.UpdateId))
        {
            context.Result = new OkResult();
            return;
        }

        var executed = await next();
        if (executed.Exception is not null || executed.Result is StatusCodeResult { StatusCode: >= 500 })
            _deduplicator.Release(update.UpdateId);
    }

    private static bool SecretsMatch(string supplied, string configured)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(configured))
            return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        return suppliedBytes.Length == configuredBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
