using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.Application.Features.Integrations.ImportTelegramSalesLists;
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
            if (result.Added == 0 && result.Reordered == 0 && result.BottleMarkers == 0) return;

            _logger.LogInformation(
                "Restored {Added}, reordered {Reordered} Next Bottle requests, and restored {BottleMarkers} imported bottle markers.",
                result.Added, result.Reordered, result.BottleMarkers);
            if (long.TryParse(_options.AdminChatId, out var adminChatId))
            {
                var message = $"✅ آرشیو اصلاح شد: {result.Added} صف بازیابی، " +
                    $"{result.Reordered} ترتیب صف و {result.BottleMarkers} علامت شیشه اصلاح شد.";
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

    private async Task<(int Added, int Reordered, int BottleMarkers)> BackfillAsync(CancellationToken cancellationToken)
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
                value.RawText,
                SalesListId = value.SalesListId!.Value
            })
            .ToArrayAsync(cancellationToken);
        if (imports.Length == 0) return (0, 0, 0);

        var listIds = imports.Select(value => value.SalesListId).Distinct().ToArray();
        var prices = await db.SalesLists.AsNoTracking()
            .Where(value => listIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.PricePerMl, cancellationToken);
        var existingRequests = await db.SalesListRequests
            .Where(value => listIds.Contains(value.SalesListId))
            .Where(value => value.ExternalReference != null)
            .ToDictionaryAsync(value => value.ExternalReference!, cancellationToken);
        var fancyBottles = await db.Bottles.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive && value.Type == BottleType.Fancy)
            .ToArrayAsync(cancellationToken);

        var added = 0;
        var reordered = 0;
        var bottleMarkers = 0;
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
            var reparsedRequests = TelegramSalesListImportParser.Parse(import.RawText).Requests;
            foreach (var (request, requestIndex) in requests.Select((request, index) => (request, index)))
            {
                if (requestIndex < reparsedRequests.Count &&
                    existingRequests.TryGetValue(
                        $"telegram-import:{import.SourceChannelId}:{import.SourceMessageId}:{requestIndex}",
                        out var markedRequest))
                {
                    var reparsed = reparsedRequests[requestIndex];
                    var changed = false;
                    if (markedRequest.OmitIdentityOnLabel != reparsed.OmitIdentityOnLabel)
                    {
                        markedRequest.OmitIdentityOnLabel = reparsed.OmitIdentityOnLabel;
                        changed = true;
                    }
                    if (reparsed.IsFancyBottle && reparsed.Kind == SalesListRequestKind.CurrentBottle &&
                        !reparsed.IsBottleOwner)
                    {
                        var bottle = fancyBottles.FirstOrDefault(value =>
                            value.VolumeMl == reparsed.VolumeMl &&
                            (string.IsNullOrWhiteSpace(reparsed.FancyBottleVariant) ||
                             value.Name.Contains(reparsed.FancyBottleVariant,
                                 StringComparison.OrdinalIgnoreCase)));
                        if (bottle is not null && markedRequest.BottleId != bottle.Id)
                        {
                            markedRequest.BottleId = bottle.Id;
                            markedRequest.BottlePrice = bottle.SalePrice;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        markedRequest.UpdatedAt = DateTime.UtcNow;
                        bottleMarkers++;
                    }
                }
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

        if (added > 0 || reordered > 0 || bottleMarkers > 0)
            await db.SaveChangesAsync(cancellationToken);
        return (added, reordered, bottleMarkers);
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
