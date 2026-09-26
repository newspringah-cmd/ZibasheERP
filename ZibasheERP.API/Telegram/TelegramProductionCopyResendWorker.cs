using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ZibasheERP.Application.Interfaces;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramProductionCopyResendWorker : BackgroundService
{
    private readonly Channel<long> _requests = Channel.CreateBounded<long>(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramMessageSender _sender;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramProductionCopyResendWorker> _logger;
    private int _pendingOrRunning;

    public TelegramProductionCopyResendWorker(
        IServiceScopeFactory scopeFactory,
        ITelegramMessageSender sender,
        IOptions<TelegramOptions> options,
        ILogger<TelegramProductionCopyResendWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
    }

    public bool TryQueue(long reportChatId)
    {
        if (Interlocked.CompareExchange(ref _pendingOrRunning, 1, 0) != 0)
            return false;
        if (_requests.Writer.TryWrite(reportChatId)) return true;
        Interlocked.Exchange(ref _pendingOrRunning, 0);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reportChatId in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ResendAllAsync(reportChatId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Resending all production copies failed.");
                await _sender.SendAsync(reportChatId.ToString(),
                    "❌ ارسال مجدد لیست‌های چاپ متوقف شد؛ جزئیات در لاگ ثبت شد.", stoppingToken);
            }
            finally
            {
                Interlocked.Exchange(ref _pendingOrRunning, 0);
            }
        }
    }

    private async Task ResendAllAsync(long reportChatId, CancellationToken cancellationToken)
    {
        ProductionCopyArchive? archive;
        using (var scope = _scopeFactory.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IInvoiceIssuanceService>();
            archive = await service.GetAllProductionCopiesAsync(cancellationToken);
        }

        if (archive is null || archive.ProductionCopies.Count == 0)
        {
            await _sender.SendAsync(reportChatId.ToString(),
                "⚠️ لیست چاپ فاکتورشده‌ای پیدا نشد.", cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.LabelPrintChatId))
        {
            await _sender.SendAsync(reportChatId.ToString(),
                "⚠️ شناسه گروه چاپ لیبل تنظیم نشده است.", cancellationToken);
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var copy in archive.ProductionCopies)
        {
            var copySucceeded = await SendCopyAsync(
                _options.LabelPrintChatId.Trim(), copy, cancellationToken);
            if (copySucceeded)
                succeeded++;
            else
                failures.Add(copy.PublicCode.ToString());

            var processed = succeeded + failures.Count;
            if (processed % 10 == 0 || processed == archive.ProductionCopies.Count)
            {
                _logger.LogInformation(
                    "Production-copy resend progress: {Processed}/{Total}; succeeded={Succeeded}; failed={Failed}.",
                    processed, archive.ProductionCopies.Count, succeeded, failures.Count);
            }
        }

        var report = failures.Count == 0
            ? $"✅ ارسال مجدد همهٔ لیست‌های چاپ پایان یافت.\nموفق: {succeeded}\nکل: {archive.ProductionCopies.Count}"
            : $"⚠️ ارسال مجدد لیست‌های چاپ پایان یافت.\nموفق: {succeeded}\nناموفق: {failures.Count}\n" +
              $"کدهای ناموفق: {string.Join("، ", failures)}";
        await _sender.SendAsync(reportChatId.ToString(), report, cancellationToken);
    }

    private async Task<bool> SendCopyAsync(
        string destinationChatId,
        SalesListProductionCopy copy,
        CancellationToken cancellationToken)
    {
        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[]
            {
                new TelegramInlineButton(
                    copy.HasPerfumeLogo ? "✅ لوگو دارد" : "🖼 ثبت لوگو",
                    $"plogo:set:{copy.SalesListId:N}"),
                new TelegramInlineButton("🖨 چاپ لوگو", $"plogo:print:{copy.SalesListId:N}")
            }
        };
        var header = $"🏷 لیست چاپ {copy.PublicCode}\n{copy.PerfumeName}";
        var headerResult = await SendWithRetryAsync(
            () => !string.IsNullOrWhiteSpace(copy.TelegramPhotoFileId)
                ? _sender.SendPhotoWithKeyboardAsync(
                    destinationChatId, copy.TelegramPhotoFileId, header, buttons, cancellationToken)
                : _sender.SendInlineKeyboardAsync(
                    destinationChatId, header + "\n⚠️ عکس عطر ثبت نشده است.", buttons, cancellationToken),
            copy.PublicCode,
            cancellationToken);
        if (!headerResult.IsSuccessful)
            return false;

        foreach (var message in SplitMessage(copy.LabelPrintMessage))
        {
            var result = await SendWithRetryAsync(
                () => _sender.SendAsync(destinationChatId, message, cancellationToken),
                copy.PublicCode,
                cancellationToken);
            if (!result.IsSuccessful)
                return false;
        }
        return true;
    }

    private async Task<TelegramSendResult> SendWithRetryAsync(
        Func<Task<TelegramSendResult>> send,
        int publicCode,
        CancellationToken cancellationToken)
    {
        TelegramSendResult? result = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            result = await send();
            if (result.IsSuccessful)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(3100), cancellationToken);
                return result;
            }
            if (!TryGetRetryAfter(result.Error, out var retryAfterSeconds) || attempt == 5)
            {
                _logger.LogWarning(
                    "Production copy {PublicCode} failed on attempt {Attempt}: {Error}",
                    publicCode, attempt, result.Error);
                return result;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds + 1, 2, 90)), cancellationToken);
        }
        return result ?? new TelegramSendResult(false, "Telegram send was not attempted.");
    }

    private static bool TryGetRetryAfter(string? error, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(error)) return false;
        var match = Regex.Match(error, @"retry after\s+(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out seconds);
    }

    private static IReadOnlyCollection<string> SplitMessage(string message)
    {
        const int maxLength = 3900;
        if (message.Length <= maxLength) return new[] { message };
        var parts = new List<string>();
        var remaining = message;
        while (remaining.Length > maxLength)
        {
            var splitAt = remaining.LastIndexOf('\n', maxLength);
            if (splitAt < maxLength / 2) splitAt = maxLength;
            parts.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }
        if (remaining.Length > 0) parts.Add(remaining);
        return parts;
    }
}
