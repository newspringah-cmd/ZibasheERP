using System.Text.Json;
using System.Text.Json.Serialization;
using ZibasheERP.Application.Features.Integrations.ImportTelegramSalesLists;

if (args.Length is < 1 or > 5)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/TelegramSalesListImport -- <result.json> [output-directory] [batch-count] [skip-count] [source-message-filter-manifest.json]");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var exportDirectory = Path.GetDirectoryName(inputPath)
    ?? throw new InvalidOperationException("The Telegram export directory could not be resolved.");
var outputDirectory = Path.GetFullPath(args.ElementAtOrDefault(1) ??
    Path.Combine(Environment.CurrentDirectory, "output", "telegram-sales-list-import"));
var batchCount = args.Length >= 3 && int.TryParse(args[2], out var requestedCount)
    ? Math.Clamp(requestedCount, 1, 10_000)
    : 20;
var skipCount = args.Length >= 4 && int.TryParse(args[3], out var requestedSkip)
    ? Math.Max(requestedSkip, 0)
    : 0;
var filterManifestPath = args.ElementAtOrDefault(4);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Telegram result.json was not found: {inputPath}");
    return 2;
}

HashSet<long>? targetMessageIds = null;
if (!string.IsNullOrWhiteSpace(filterManifestPath))
{
    filterManifestPath = Path.GetFullPath(filterManifestPath);
    if (!File.Exists(filterManifestPath))
    {
        Console.Error.WriteLine($"Source message filter manifest was not found: {filterManifestPath}");
        return 2;
    }

    await using var filterInput = File.OpenRead(filterManifestPath);
    var filterItems = await JsonSerializer.DeserializeAsync<IReadOnlyList<SourceMessageFilterItem>>(
        filterInput, JsonDefaults.Options) ?? [];
    targetMessageIds = filterItems.Select(value => value.SourceMessageId).ToHashSet();
    if (targetMessageIds.Count == 0)
    {
        Console.Error.WriteLine("Source message filter manifest did not contain any message IDs.");
        return 2;
    }
}

Directory.CreateDirectory(outputDirectory);
await using var input = File.OpenRead(inputPath);
var export = await JsonSerializer.DeserializeAsync<TelegramExport>(input, JsonDefaults.Options)
    ?? throw new InvalidOperationException("Telegram export JSON is empty or invalid.");

var candidates = new List<ImportManifestItem>();
var issues = new Dictionary<string, int>(StringComparer.Ordinal);
var photoMessages = 0;
var matchedTargetMessageIds = new HashSet<long>();

foreach (var message in export.Messages.OrderBy(value => value.Id))
{
    if (targetMessageIds is not null && !targetMessageIds.Contains(message.Id)) continue;
    matchedTargetMessageIds.Add(message.Id);
    if (string.IsNullOrWhiteSpace(message.Photo)) continue;
    photoMessages++;

    var text = ExtractText(message.Text);
    if (string.IsNullOrWhiteSpace(text))
    {
        AddIssue(issues, "photo_without_caption");
        continue;
    }

    var parsed = TelegramSalesListImportParser.Parse(text);
    foreach (var issue in parsed.Issues) AddIssue(issues, issue);

    var relativePhotoPath = message.Photo!.Replace('/', Path.DirectorySeparatorChar);
    var absolutePhotoPath = Path.GetFullPath(Path.Combine(exportDirectory, relativePhotoPath));
    var photoExists = File.Exists(absolutePhotoPath);
    if (!photoExists) AddIssue(issues, "photo_file_missing");

    candidates.Add(new ImportManifestItem(
        export.Id.ToString(), message.Id, message.Date, relativePhotoPath, photoExists,
        text, parsed, parsed.IsSafeForAutomaticReview && photoExists));
}

var allSafe = candidates.Where(value => value.IsSafeForPilot).ToArray();
var safe = allSafe.Skip(skipCount).Take(batchCount).ToArray();
var review = candidates.Where(value => !value.IsSafeForPilot).ToArray();

await WriteJsonAsync(Path.Combine(outputDirectory, "pilot-manifest.json"), safe);
await WriteJsonAsync(Path.Combine(outputDirectory, "manual-review-manifest.json"), review);
await WriteJsonAsync(Path.Combine(outputDirectory, "import-summary.json"), new
{
    export.Name,
    SourceChannelId = export.Id.ToString(),
    TotalMessages = export.Messages.Count,
    PhotoMessages = photoMessages,
    ParsedCandidates = candidates.Count,
    SafeCandidates = allSafe.Length,
    SkippedSafeCandidates = Math.Min(skipCount, allSafe.Length),
    PilotItems = safe.Length,
    RemainingSafeCandidates = Math.Max(allSafe.Length - skipCount - safe.Length, 0),
    ManualReviewItems = review.Length,
    FilteredSourceMessageIds = targetMessageIds?.Count,
    MatchedFilteredMessages = targetMessageIds is null ? (int?)null : matchedTargetMessageIds.Count,
    MissingFilteredMessages = targetMessageIds is null
        ? null
        : targetMessageIds.Except(matchedTargetMessageIds).Order().ToArray(),
    Issues = issues.OrderByDescending(value => value.Value)
});

Console.WriteLine($"Channel: {export.Name} ({export.Id})");
Console.WriteLine($"Photo messages: {photoMessages}");
Console.WriteLine($"Parsed candidates: {candidates.Count}");
Console.WriteLine($"Safe candidates: {candidates.Count(value => value.IsSafeForPilot)}");
Console.WriteLine($"Skipped safe candidates: {Math.Min(skipCount, allSafe.Length)}");
Console.WriteLine($"Pilot manifest: {safe.Length} item(s)");
Console.WriteLine($"Remaining safe candidates: {Math.Max(allSafe.Length - skipCount - safe.Length, 0)}");
Console.WriteLine($"Manual review: {review.Length} item(s)");
if (targetMessageIds is not null)
{
    Console.WriteLine($"Filtered source messages: {targetMessageIds.Count}");
    Console.WriteLine($"Matched filtered messages: {matchedTargetMessageIds.Count}");
    Console.WriteLine($"Missing filtered messages: {targetMessageIds.Except(matchedTargetMessageIds).Count()}");
}
Console.WriteLine($"Output: {outputDirectory}");
return safe.Length > 0 ? 0 : 1;

static string ExtractText(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
    if (value.ValueKind != JsonValueKind.Array) return string.Empty;

    return string.Concat(value.EnumerateArray().Select(part => part.ValueKind switch
    {
        JsonValueKind.String => part.GetString(),
        JsonValueKind.Object when part.TryGetProperty("text", out var text) => text.GetString(),
        _ => string.Empty
    }));
}

static void AddIssue(IDictionary<string, int> issues, string issue) =>
    issues[issue] = issues.TryGetValue(issue, out var count) ? count + 1 : 1;

static async Task WriteJsonAsync(string path, object value)
{
    await using var output = File.Create(path);
    await JsonSerializer.SerializeAsync(output, value, JsonDefaults.Options);
}

internal sealed record TelegramExport(
    string Name,
    long Id,
    IReadOnlyList<TelegramExportMessage> Messages);

internal sealed record TelegramExportMessage(
    long Id,
    DateTimeOffset Date,
    string? Photo,
    JsonElement Text);

internal sealed record ImportManifestItem(
    string SourceChannelId,
    long SourceMessageId,
    DateTimeOffset SourceDate,
    string PhotoPath,
    bool PhotoExists,
    string RawText,
    TelegramSalesListImportParseResult Parsed,
    bool IsSafeForPilot);

internal sealed record SourceMessageFilterItem(long SourceMessageId);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
