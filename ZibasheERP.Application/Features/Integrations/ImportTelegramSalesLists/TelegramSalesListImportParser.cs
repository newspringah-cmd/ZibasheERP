using System.Globalization;
using System.Text.RegularExpressions;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Integrations.ImportTelegramSalesLists;

public sealed record TelegramSalesListImportRequest(
    string TelegramUsername,
    int VolumeMl,
    SalesListRequestKind Kind,
    bool IsBottleOwner = false,
    string? GiftRecipientTelegramUsername = null);

public sealed record TelegramSalesListImportParseResult(
    int? PublicCode,
    string EnglishName,
    string DisplayBrand,
    PerfumeGender Gender,
    int? ReleaseYear,
    string PersianName,
    string TopNotes,
    string MiddleNotes,
    string BaseNotes,
    string Accords,
    string? ProductPageUrl,
    int? TotalVolumeMl,
    decimal? PricePerMl,
    int? MinimumRequestVolumeMl,
    int? DeclaredRemainingVolumeMl,
    IReadOnlyList<TelegramSalesListImportRequest> Requests,
    IReadOnlyList<string> Issues)
{
    public bool IsSafeForAutomaticReview =>
        PublicCode is > 0 &&
        !string.IsNullOrWhiteSpace(EnglishName) &&
        !string.IsNullOrWhiteSpace(DisplayBrand) &&
        ReleaseYear is > 0 &&
        !string.IsNullOrWhiteSpace(PersianName) &&
        TotalVolumeMl is > 0 &&
        PricePerMl is > 0 &&
        MinimumRequestVolumeMl is > 0 &&
        DeclaredRemainingVolumeMl is >= 0 &&
        Issues.Count == 0;
}

public static partial class TelegramSalesListImportParser
{
    private const int DefaultNextBottleVolumeMl = 30;

    [GeneratedRegex(@"(?im)^\s*کد\s*:\s*(?<value>[۰-۹0-9]+)\s*$")]
    private static partial Regex PublicCodeRegex();

    [GeneratedRegex(@"(?im)^\s*(?<value>[۰-۹0-9]+)\s*ml\s*$")]
    private static partial Regex TotalVolumeRegex();

    [GeneratedRegex(@"(?im)قیمت\s*هر\s*میل\s*:?\s*(?<value>[۰-۹0-9][۰-۹0-9/,.٬]*)")]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"(?im)حداقل\s*میل\s*درخواستی\s*:\s*(?<value>[۰-۹0-9]+)\s*(?:میل|ميل)")]
    private static partial Regex MinimumVolumeRegex();

    [GeneratedRegex(@"(?im)باق[یي]مانده\s*:\s*(?<value>[۰-۹0-9]+)\s*(?:میل|ميل)")]
    private static partial Regex RemainingVolumeRegex();

    [GeneratedRegex(@"(?im)^\s*L\s*\.\s*(?<value>[۱۲۳۴۵۶۷۸۹۰0-9]{4})\s*$")]
    private static partial Regex ReleaseYearRegex();

    [GeneratedRegex(@"(?im)^\s*#(?<value>[A-Za-z][A-Za-z0-9_. -]+)\s*$")]
    private static partial Regex BrandRegex();

    [GeneratedRegex(@"(?i)@(?<value>[A-Za-z0-9_]{3,})")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"(?i)(?<value>[۰-۹0-9]+)\s*(?:ml|میل|ميل)")]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"(?i)^\s*(?<volume>[۰-۹0-9]+)\s*(?:ml|میل|ميل)\s*:\s*(?<tail>.*)$")]
    private static partial Regex VolumeHeadingRegex();

    public static TelegramSalesListImportParseResult Parse(string? source)
    {
        var text = Normalize(source);
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        var issues = new List<string>();

        var code = MatchInt(PublicCodeRegex(), text);
        var totalVolume = MatchInt(TotalVolumeRegex(), text);
        var price = MatchDecimal(PriceRegex(), text);
        var minimum = MatchInt(MinimumVolumeRegex(), text);
        var remaining = MatchInt(RemainingVolumeRegex(), text);
        var year = MatchInt(ReleaseYearRegex(), text);
        var englishName = lines.FirstOrDefault(IsEnglishNameLine) ?? string.Empty;
        var brand = BrandRegex().Match(text) is { Success: true } brandMatch
            ? brandMatch.Groups["value"].Value.Trim().Replace('_', ' ')
            : string.Empty;
        var persianName = FindPersianName(lines);
        var topNotes = FindSection(text, "نت های اولیه", "نت‌های اولیه", "نت‌های ابتدایی", "نت های ابتدایی");
        var middleNotes = FindSection(text, "نت های میانی", "نت‌های میانی");
        var baseNotes = FindSection(text, "نت های پایه", "نت‌های پایه", "نت‌های پایانی", "نت های پایانی");
        var accords = FindLineAfterLabel(lines, "آکوردها", "آکورد ها", "اکوردها", "اکورد ها");
        var productUrl = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase) is { Success: true } url
            ? url.Value.TrimEnd('.', ',', ')') : null;
        var gender = text.Contains("#forwomen", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("#formen", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("#unisex", StringComparison.OrdinalIgnoreCase)
            ? PerfumeGender.Unisex
            : text.Contains("#forwomen", StringComparison.OrdinalIgnoreCase)
                ? PerfumeGender.Women
                : text.Contains("#formen", StringComparison.OrdinalIgnoreCase)
                    ? PerfumeGender.Men
                    : PerfumeGender.Unisex;

        if (code is null) issues.Add("missing_public_code");
        if (string.IsNullOrWhiteSpace(englishName)) issues.Add("missing_english_name");
        if (totalVolume is null) issues.Add("missing_total_volume");
        if (price is null) issues.Add("missing_price_per_ml");

        var requests = ParseRequests(lines, issues);
        if (requests.Count == 0) issues.Add("no_requests_detected");
        if (totalVolume.HasValue && remaining.HasValue)
        {
            var currentVolume = requests
                .Where(value => value.Kind == SalesListRequestKind.CurrentBottle)
                .Sum(value => value.VolumeMl);
            if (currentVolume + remaining.Value != totalVolume.Value)
                issues.Add("reserved_volume_mismatch");
        }

        return new TelegramSalesListImportParseResult(
            code, englishName, brand, gender, year, persianName, topNotes, middleNotes, baseNotes, accords, productUrl, totalVolume,
            price, minimum, remaining, requests, issues.Distinct().ToArray());
    }

    private static List<TelegramSalesListImportRequest> ParseRequests(
        IReadOnlyList<string> lines,
        ICollection<string> issues)
    {
        var result = new List<TelegramSalesListImportRequest>();
        var kind = SalesListRequestKind.CurrentBottle;
        int? headingVolume = null;
        var bottleSection = false;
        var bottleOwnerAssigned = false;
        var requestSectionStarted = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("Next Bottle", StringComparison.OrdinalIgnoreCase))
            {
                kind = SalesListRequestKind.NextBottle;
                headingVolume = null;
                requestSectionStarted = true;
                continue;
            }

            if (line.StartsWith("Bottle:", StringComparison.OrdinalIgnoreCase))
            {
                bottleSection = true;
                requestSectionStarted = true;
                ParseRequestLine(line["Bottle:".Length..], null, kind, true, result, issues);
                bottleOwnerAssigned = result.Count > 0;
                continue;
            }

            var heading = VolumeHeadingRegex().Match(line);
            if (heading.Success)
            {
                headingVolume = ParseInt(heading.Groups["volume"].Value);
                requestSectionStarted = true;
                ParseRequestLine(heading.Groups["tail"].Value, headingVolume, kind, false, result, issues);
                continue;
            }

            if (!requestSectionStarted || !UsernameRegex().IsMatch(line))
                continue;

            if (line.TrimStart().StartsWith("for @", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("ambiguous_standalone_gift_recipient");
                continue;
            }

            var before = result.Count;
            ParseRequestLine(
                line,
                headingVolume,
                kind,
                bottleSection && !bottleOwnerAssigned,
                result,
                issues);
            if (result.Count > before && bottleSection)
                bottleOwnerAssigned = true;
        }

        return result;
    }

    private static void ParseRequestLine(
        string line,
        int? inheritedVolume,
        SalesListRequestKind kind,
        bool isBottleOwner,
        ICollection<TelegramSalesListImportRequest> target,
        ICollection<string> issues)
    {
        var users = UsernameRegex().Matches(line)
            .Select(match => match.Groups["value"].Value)
            .ToArray();
        if (users.Length == 0) return;

        var volume = MatchInt(VolumeRegex(), line) ?? inheritedVolume;
        if (volume is null && kind == SalesListRequestKind.NextBottle)
            volume = DefaultNextBottleVolumeMl;
        if (volume is null or <= 0)
        {
            issues.Add("request_without_volume");
            return;
        }

        var isGift = line.Contains(" for @", StringComparison.OrdinalIgnoreCase);
        if (isGift)
        {
            if (users.Length < 2)
            {
                issues.Add("invalid_gift_request");
                return;
            }
            target.Add(new TelegramSalesListImportRequest(users[0], volume.Value, kind,
                isBottleOwner, users[1]));
            return;
        }

        foreach (var user in users)
            target.Add(new TelegramSalesListImportRequest(user, volume.Value, kind,
                isBottleOwner && target.Count == 0));
    }

    private static bool IsEnglishNameLine(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.Any(char.IsLetter) &&
        line.All(character => character < 0x0600 || char.IsWhiteSpace(character)) &&
        !line.StartsWith("کد", StringComparison.Ordinal) &&
        !line.StartsWith("L.", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("Bottle", StringComparison.OrdinalIgnoreCase) &&
        !line.StartsWith("Next Bottle", StringComparison.OrdinalIgnoreCase);

    private static string FindPersianName(IReadOnlyList<string> lines)
    {
        var yearIndex = Array.FindIndex(lines.ToArray(), line => ReleaseYearRegex().IsMatch(line));
        if (yearIndex < 0) return string.Empty;
        return lines.Skip(yearIndex + 1)
            .FirstOrDefault(line => line.Any(character => character is >= '\u0600' and <= '\u06ff') &&
                                    !line.Contains("نت", StringComparison.Ordinal)) ?? string.Empty;
    }

    private static string FindSection(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = text[(index + label.Length)..].TrimStart(" :🍊🌹🌬️🪵🌸💐🌶️".ToCharArray());
            var boundaries = new[] { "نت های اولیه", "نت‌های اولیه", "نت‌های ابتدایی", "نت های ابتدایی",
                "نت های میانی", "نت‌های میانی", "نت های پایه", "نت‌های پایه", "نت‌های پایانی", "نت های پایانی",
                "آکوردها", "آکورد ها", "اکوردها", "اکورد ها", "100ml", "قیمت هر میل", "حداقل میل" };
            var boundary = boundaries
                .Where(other => !string.Equals(other, label, StringComparison.OrdinalIgnoreCase))
                .Select(other => value.IndexOf(other, StringComparison.OrdinalIgnoreCase))
                .Where(position => position >= 0)
                .DefaultIfEmpty(value.Length)
                .Min();
            value = value[..boundary];
            value = Regex.Replace(value, @"\s{2,}", " ").Trim();
            return value.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string FindLineAfterLabel(IReadOnlyList<string> lines, params string[] labels)
    {
        foreach (var label in labels)
        {
            var index = Array.FindIndex(lines.ToArray(), line => line.StartsWith(label, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                return lines[index][label.Length..].Trim(' ', ':');
        }

        return string.Empty;
    }

    private static int? MatchInt(Regex regex, string text) =>
        regex.Match(text) is { Success: true } match
            ? ParseInt(match.Groups["value"].Value)
            : null;

    private static decimal? MatchDecimal(Regex regex, string text)
    {
        var match = regex.Match(text);
        if (!match.Success) return null;
        var normalized = ToLatinDigits(match.Groups["value"].Value)
            .Replace("/", string.Empty).Replace(",", string.Empty).Replace(".", string.Empty)
            .Replace("٬", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(ToLatinDigits(value), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Replace('ي', 'ی');

    private static string ToLatinDigits(string value)
    {
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        const string arabic = "٠١٢٣٤٥٦٧٨٩";
        return string.Concat(value.Select(character =>
        {
            var index = persian.IndexOf(character);
            if (index < 0) index = arabic.IndexOf(character);
            return index >= 0 ? (char)('0' + index) : character;
        }));
    }
}
