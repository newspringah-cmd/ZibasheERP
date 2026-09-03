using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramSalesListRebuildWorker : BackgroundService
{
    private readonly Channel<long> _requests = Channel.CreateBounded<long>(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramMessageSender _sender;
    private readonly ILogger<TelegramSalesListRebuildWorker> _logger;
    private int _pendingOrRunning;

    public TelegramSalesListRebuildWorker(
        IServiceScopeFactory scopeFactory,
        ITelegramMessageSender sender,
        ILogger<TelegramSalesListRebuildWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
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
                await RebuildAllAsync(reportChatId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Rebuilding Telegram sales-list posts failed.");
                await _sender.SendAsync(reportChatId.ToString(),
                    $"❌ بازسازی پست‌های لیست ناموفق بود: {exception.Message}", stoppingToken);
            }
            finally
            {
                Interlocked.Exchange(ref _pendingOrRunning, 0);
            }
        }
    }

    private async Task RebuildAllAsync(long reportChatId, CancellationToken cancellationToken)
    {
        Guid[] listIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            listIds = await db.SalesLists.AsNoTracking()
                .Where(value => !value.IsDeleted &&
                    (value.Status == SalesListStatus.Open || value.Status == SalesListStatus.Full) &&
                    value.TelegramMessageId.HasValue && value.TelegramChannelId != null)
                .OrderBy(value => value.OpenDate)
                .Select(value => value.Id)
                .ToArrayAsync(cancellationToken);
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var listId in listIds)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lists = scope.ServiceProvider.GetRequiredService<ISalesListRepository>();
                var requests = scope.ServiceProvider.GetRequiredService<ISalesListRequestRepository>();
                var list = await lists.GetByIdAsync(listId, cancellationToken);
                if (list is null || !list.TelegramMessageId.HasValue ||
                    string.IsNullOrWhiteSpace(list.TelegramChannelId))
                    continue;

                var activeRequests = await requests.GetConfirmedAsync(list.Id, cancellationToken);
                var captions = TelegramWebhookController.FormatChannelSalesListPages(list, activeRequests);
                await SynchronizeContinuationAsync(list, captions.Continuation, lists, cancellationToken);
                var result = await _sender.EditPhotoCaptionAsync(
                    list.TelegramChannelId,
                    list.TelegramMessageId.Value,
                    captions.Main,
                    TelegramWebhookController.BuildChannelVolumeButtons(list),
                    cancellationToken);
                if (result.IsSuccessful || IsUnchanged(result.Error)) succeeded++;
                else
                {
                    failed++;
                    _logger.LogWarning("Sales-list post {PublicCode} was not rebuilt: {Error}",
                        list.PublicCode, result.Error);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                _logger.LogWarning(exception, "Sales-list post {SalesListId} was not rebuilt.", listId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
        }

        await _sender.SendAsync(reportChatId.ToString(),
            $"✅ بازسازی پست‌های لیست پایان یافت.\nموفق: {succeeded}\nناموفق: {failed}\nکل: {listIds.Length}",
            cancellationToken);
    }

    private async Task SynchronizeContinuationAsync(
        SalesList list,
        string? continuation,
        ISalesListRepository repository,
        CancellationToken cancellationToken)
    {
        var channelId = list.TelegramChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || !list.TelegramMessageId.HasValue)
            return;
        if (string.IsNullOrWhiteSpace(continuation))
        {
            if (!list.TelegramContinuationMessageId.HasValue) return;
            await _sender.DeleteMessageAsync(
                channelId, list.TelegramContinuationMessageId.Value, cancellationToken);
            list.TelegramContinuationMessageId = null;
            await repository.UpdateAsync(list, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
            return;
        }

        var mainUrl = BuildMessageUrl(channelId, list.TelegramMessageId.Value);
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> navigation =
            new[] { (IReadOnlyCollection<TelegramInlineButton>)new[]
                { new TelegramInlineButton("⬅️ بازگشت به پست اصلی", Url: mainUrl) } };
        if (list.TelegramContinuationMessageId.HasValue)
        {
            await _sender.EditPhotoCaptionAsync(
                channelId, list.TelegramContinuationMessageId.Value,
                continuation, navigation, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)) return;
        var result = await _sender.SendPhotoWithKeyboardAsync(
            channelId, list.TelegramPhotoFileId,
            continuation, navigation, cancellationToken);
        if (!result.IsSuccessful || !result.MessageId.HasValue) return;
        list.TelegramContinuationMessageId = result.MessageId.Value;
        await repository.UpdateAsync(list, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private static string BuildMessageUrl(string chatId, long messageId) =>
        $"https://t.me/c/{chatId.Trim()[4..]}/{messageId}";

    private static bool IsUnchanged(string? error) =>
        error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;
}
