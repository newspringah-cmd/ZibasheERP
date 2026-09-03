using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Telegram;

public sealed class TelegramImportedNextBottleBackfillWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelegramSalesListRebuildWorker _rebuildWorker;
    private readonly ITelegramMessageSender _sender;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramImportedNextBottleBackfillWorker> _logger;

    public TelegramImportedNextBottleBackfillWorker(
        IServiceScopeFactory scopeFactory,
        TelegramSalesListRebuildWorker rebuildWorker,
        ITelegramMessageSender sender,
        IOptions<TelegramOptions> options,
        ILogger<TelegramImportedNextBottleBackfillWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _rebuildWorker = rebuildWorker;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var added = await BackfillAsync(stoppingToken);
            if (added == 0) return;

            _logger.LogInformation("Restored {Count} imported Next Bottle requests.", added);
            if (long.TryParse(_options.AdminChatId, out var adminChatId))
            {
                await _sender.SendAsync(_options.AdminChatId,
                    $"✅ {added} مورد صف Next Bottle جاافتاده از انتقال‌های قبلی بازیابی شد.", stoppingToken);
                _rebuildWorker.TryQueue(adminChatId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backfilling imported Next Bottle requests failed.");
        }
    }

    private async Task<int> BackfillAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imports = await db.TelegramSalesListImports.AsNoTracking()
            .Where(value => !value.IsDeleted && value.SalesListId.HasValue &&
                (value.Status == TelegramSalesListImportStatus.Imported ||
                 value.Status == TelegramSalesListImportStatus.Published))
            .Select(value => new
            {
                value.SourceChannelId,
                value.SourceMessageId,
                value.SourceDate,
                value.ParsedPayload,
                SalesListId = value.SalesListId!.Value
            })
            .ToArrayAsync(cancellationToken);
        if (imports.Length == 0) return 0;

        var listIds = imports.Select(value => value.SalesListId).Distinct().ToArray();
        var prices = await db.SalesLists.AsNoTracking()
            .Where(value => listIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.PricePerMl, cancellationToken);
        var existingReferences = await db.SalesListRequests.AsNoTracking()
            .Where(value => listIds.Contains(value.SalesListId))
            .Select(value => value.ExternalReference)
            .ToHashSetAsync(cancellationToken);

        var added = 0;
        foreach (var import in imports)
        {
            if (!prices.TryGetValue(import.SalesListId, out var price)) continue;
            using var document = JsonDocument.Parse(import.ParsedPayload);
            if (!document.RootElement.TryGetProperty("requests", out var requestArray) ||
                requestArray.ValueKind != JsonValueKind.Array)
                continue;
            var requests = JsonSerializer.Deserialize<List<ImportedRequest>>(
                requestArray.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var (request, requestIndex) in requests.Select((request, index) => (request, index)))
            {
                if (request.Kind != SalesListRequestKind.NextBottle ||
                    request.VolumeMl <= 0 || string.IsNullOrWhiteSpace(request.TelegramUsername))
                    continue;
                var externalReference =
                    $"telegram-import:{import.SourceChannelId}:{import.SourceMessageId}:{requestIndex}";
                if (!existingReferences.Add(externalReference)) continue;
                var username = request.TelegramUsername.Trim().TrimStart('@');
                var giftRecipient = string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
                    ? null
                    : request.GiftRecipientTelegramUsername.Trim().TrimStart('@');
                db.SalesListRequests.Add(new SalesListRequest
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = import.SourceDate.UtcDateTime,
                    SalesListId = import.SalesListId,
                    TelegramUsername = username,
                    TelegramUserId = $"imported:{username.ToLowerInvariant()}",
                    VolumeMl = request.VolumeMl,
                    IsBottleOwner = false,
                    IsGift = giftRecipient is not null,
                    GiftRecipientTelegramUsername = giftRecipient,
                    Kind = SalesListRequestKind.NextBottle,
                    Status = SalesListRequestStatus.Confirmed,
                    CreatedByAdmin = true,
                    ConfirmedAt = import.SourceDate.UtcDateTime,
                    ExpiresAt = DateTime.MaxValue,
                    PerfumePricePerMl = price,
                    ExternalReference = externalReference
                });
                added++;
            }
        }

        if (added > 0)
            await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private sealed record ImportedRequest(
        string TelegramUsername,
        int VolumeMl,
        SalesListRequestKind Kind,
        bool IsBottleOwner,
        string? GiftRecipientTelegramUsername);
}
