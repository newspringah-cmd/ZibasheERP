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
            var result = await BackfillAsync(stoppingToken);
            if (result.Added == 0 && result.Reordered == 0) return;

            _logger.LogInformation(
                "Restored {Added} and reordered {Reordered} imported Next Bottle requests.",
                result.Added, result.Reordered);
            if (long.TryParse(_options.AdminChatId, out var adminChatId))
            {
                var message = result.Added > 0
                    ? $"✅ {result.Added} مورد صف Next Bottle جاافتاده بازیابی و {result.Reordered} مورد با ترتیب اصلی اصلاح شد."
                    : $"✅ ترتیب {result.Reordered} مورد صف Next Bottle مطابق متن اصلی انتقال اصلاح شد.";
                await _sender.SendAsync(_options.AdminChatId, message, stoppingToken);
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

    private async Task<(int Added, int Reordered)> BackfillAsync(CancellationToken cancellationToken)
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
        if (imports.Length == 0) return (0, 0);

        var listIds = imports.Select(value => value.SalesListId).Distinct().ToArray();
        var prices = await db.SalesLists.AsNoTracking()
            .Where(value => listIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.PricePerMl, cancellationToken);
        var existingRequests = await db.SalesListRequests
            .Where(value => listIds.Contains(value.SalesListId))
            .Where(value => value.ExternalReference != null)
            .ToDictionaryAsync(value => value.ExternalReference!, cancellationToken);

        var added = 0;
        var reordered = 0;
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
                var importedAt = import.SourceDate.UtcDateTime.AddTicks(requestIndex);
                if (existingRequests.TryGetValue(externalReference, out var existing))
                {
                    if (existing.Kind == SalesListRequestKind.NextBottle &&
                        (existing.CreatedAt != importedAt || existing.ConfirmedAt != importedAt))
                    {
                        existing.CreatedAt = importedAt;
                        existing.ConfirmedAt = importedAt;
                        existing.UpdatedAt = DateTime.UtcNow;
                        reordered++;
                    }
                    continue;
                }
                var username = request.TelegramUsername.Trim().TrimStart('@');
                var giftRecipient = string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
                    ? null
                    : request.GiftRecipientTelegramUsername.Trim().TrimStart('@');
                db.SalesListRequests.Add(new SalesListRequest
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = importedAt,
                    SalesListId = import.SalesListId,
                    TelegramUsername = username,
                    TelegramUserId = request.IsExternalIdentity
                        ? $"imported-external:{username.ToLowerInvariant()}"
                        : $"imported:{username.ToLowerInvariant()}",
                    VolumeMl = request.VolumeMl,
                    IsBottleOwner = false,
                    IsGift = giftRecipient is not null,
                    GiftRecipientTelegramUsername = giftRecipient,
                    GiftRecipientTelegramUserId = giftRecipient is null
                        ? null
                        : request.GiftRecipientIsExternalIdentity
                            ? $"imported-external:{giftRecipient.ToLowerInvariant()}"
                            : $"imported:{giftRecipient.ToLowerInvariant()}",
                    Kind = SalesListRequestKind.NextBottle,
                    Status = SalesListRequestStatus.Confirmed,
                    CreatedByAdmin = true,
                    ConfirmedAt = importedAt,
                    ExpiresAt = DateTime.MaxValue,
                    PerfumePricePerMl = price,
                    ExternalReference = externalReference
                });
                added++;
            }
        }

        if (added > 0 || reordered > 0)
            await db.SaveChangesAsync(cancellationToken);
        return (added, reordered);
    }

    private sealed record ImportedRequest(
        string TelegramUsername,
        int VolumeMl,
        SalesListRequestKind Kind,
        bool IsBottleOwner,
        string? GiftRecipientTelegramUsername,
        bool IsExternalIdentity = false,
        bool GiftRecipientIsExternalIdentity = false);
}
