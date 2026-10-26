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
        using var scope = _scopeFactory.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<TelegramProductionCopyDelivery>();
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
        var result = await delivery.SendAsync(copy.SalesListId, destinationChatId,
            copy.TelegramPhotoFileId, header, buttons, copy.LabelPrintMessage, cancellationToken);
        if (result.IsSuccessful)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(3100), cancellationToken);
            return true;
        }

        _logger.LogWarning("Production copy {PublicCode} was not sent or needs review: {Error}",
            copy.PublicCode, result.Error);
        return false;
    }

}
