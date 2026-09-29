using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ZibasheERP.API.AddressLabels;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Tracking;

public sealed record TrackingImportItem(
    TrackingCarrier Carrier,
    string TrackingCode,
    string RecipientName,
    string Destination,
    string? TrackingUrl,
    byte[] CardImage,
    bool IsSafeForAutomaticDelivery = true,
    string? SafetyNote = null);

public sealed record TrackingImportParseResult(
    bool IsSuccessful,
    IReadOnlyCollection<TrackingImportItem> Items,
    string? Error = null);

public sealed record TrackingMatch(
    Guid? CustomerId,
    Guid? ShippingRequestId,
    TrackingDispatchStatus Status,
    string Notes);

public interface ITrackingImportService
{
    Task<TrackingImportParseResult> ParseChaparAsync(string text, CancellationToken ct);
    Task<TrackingImportParseResult> ParseIranPostExpressAsync(string text, CancellationToken ct);
    Task<TrackingImportParseResult> ParseIranPostPdfAsync(byte[] pdf, CancellationToken ct);
    Task<TrackingMatch> MatchAsync(string recipientName, string destination, CancellationToken ct);
    Task<string?> RecheckRecipientNameAsync(byte[] cardImage, CancellationToken ct);
    byte[] BuildCard(TrackingCarrier carrier, string recipientName, string trackingCode, string? trackingUrl);
}

public sealed partial class TrackingImportService : ITrackingImportService, IDisposable
{
    private const string FontFamily = "B Nazanin";
    private readonly AppDbContext _db;
    private readonly AddressLabelOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TrackingImportService> _logger;
    private readonly HttpClient _httpClient;

    public TrackingImportService(
        AppDbContext db,
        IOptions<AddressLabelOptions> options,
        IWebHostEnvironment environment,
        ILogger<TrackingImportService> logger)
    {
        _db = db;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromMinutes(3)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);
    }

    public Task<TrackingImportParseResult> ParseChaparAsync(string text, CancellationToken ct)
    {
        try
        {
            var normalized = text.Replace('\u00A0', ' ').Replace('\u200F'.ToString(), string.Empty);
            var matches = ChaparBlockPattern().Matches(normalized);
            var items = new List<TrackingImportItem>();
            foreach (Match match in matches)
            {
                ct.ThrowIfCancellationRequested();
                var block = match.Value;
                var codeMatch = ChaparCodePattern().Match(block);
                var nameMatch = ChaparNamePattern().Match(block);
                var destinationMatch = ChaparDestinationPattern().Match(block);
                var urlMatch = ChaparUrlPattern().Match(block);
                var code = NormalizeDigits(codeMatch.Groups["value"].Value);
                var name = Clean(nameMatch.Groups["value"].Value);
                var destination = Clean(destinationMatch.Groups["value"].Value);
                var url = urlMatch.Success
                    ? urlMatch.Value.Trim().TrimEnd('.', '،', ')', ']', ' ')
                    : null;
                if (code.Length is < 10 or > 30 || string.IsNullOrWhiteSpace(name))
                    continue;
                byte[] card;
                try
                {
                    card = BuildChaparCard(name, code, url);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Chapar tracking card generation failed for {TrackingCode}.", code);
                    return Task.FromResult(new TrackingImportParseResult(false, [],
                        $"مرحله ساخت تصویر کد رهگیری چاپار برای «{name}» ناموفق بود؛ هیچ پیامی ارسال نشد."));
                }
                items.Add(new TrackingImportItem(TrackingCarrier.Chapar, code, name, destination, url, card));
            }

            if (items.Count == 0)
                return Task.FromResult(new TrackingImportParseResult(false, [],
                    "مرحله خواندن متن چاپار ناموفق بود: نام گیرنده یا کد رهگیری معتبری پیدا نشد؛ هیچ پیامی ارسال نشد."));
            if (items.Count > 100)
                return Task.FromResult(new TrackingImportParseResult(false, [],
                    "مرحله اعتبارسنجی متوقف شد: حداکثر ۱۰۰ مرسوله را در هر مرحله وارد کنید؛ هیچ پیامی ارسال نشد."));
            var duplicates = items.GroupBy(value => value.TrackingCode).Where(value => value.Count() > 1)
                .Select(value => value.Key).ToArray();
            if (duplicates.Length > 0)
                return Task.FromResult(new TrackingImportParseResult(false, [],
                    $"مرحله کنترل تکراری متوقف شد: کد تکراری در همین متن پیدا شد ({string.Join("، ", duplicates)})؛ هیچ پیامی ارسال نشد."));
            return Task.FromResult(new TrackingImportParseResult(true, items));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Chapar tracking text parsing failed.");
            return Task.FromResult(new TrackingImportParseResult(false, [],
                "مرحله پردازش متن چاپار با خطای فنی روبه‌رو شد؛ هیچ پیامی ارسال نشد و جزئیات در لاگ ثبت شد."));
        }
    }

    public async Task<TrackingImportParseResult> ParseIranPostExpressAsync(
        string text, CancellationToken ct)
    {
        try
        {
            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
                return new TrackingImportParseResult(false, [],
                    "سرویس OpenAI برای خواندن متن پست ویژه فعال نیست؛ هیچ پیامی ارسال نشد.");
            if (string.IsNullOrWhiteSpace(text))
                return new TrackingImportParseResult(false, [],
                    "مرحله خواندن متن پست ویژه ناموفق بود: متن خالی است؛ هیچ پیامی ارسال نشد.");

            var rows = await RecognizePostExpressRowsAsync(text, ct);
            if (rows.Length == 0)
                return new TrackingImportParseResult(false, [],
                    "مرحله خواندن متن پست ویژه ناموفق بود: نام گیرنده و کد رهگیری پیدا نشد؛ هیچ پیامی ارسال نشد.");
            if (rows.Length > 100)
                return new TrackingImportParseResult(false, [],
                    "مرحله اعتبارسنجی متوقف شد: حداکثر ۱۰۰ مرسوله را در هر مرحله وارد کنید؛ هیچ پیامی ارسال نشد.");

            var normalizedInput = NormalizeName(text);
            var inputDigits = NormalizeDigits(text);
            var items = new List<TrackingImportItem>();
            foreach (var row in rows)
            {
                var code = NormalizeDigits(row.TrackingCode);
                var name = Clean(row.RecipientName);
                var codeExists = code.Length is >= 15 and <= 30 &&
                                 inputDigits.Contains(code, StringComparison.Ordinal);
                var nameExists = NormalizeName(name).Length >= 3 &&
                                 normalizedInput.Contains(NormalizeName(name), StringComparison.Ordinal);
                if (!codeExists)
                    return new TrackingImportParseResult(false, [],
                        $"مرحله اعتبارسنجی متن پست ویژه متوقف شد: کد «{row.TrackingCode}» عیناً در متن ورودی پیدا نشد؛ هیچ پیامی ارسال نشد.");
                var safe = nameExists && row.Confidence >= .90;
                items.Add(new TrackingImportItem(
                    TrackingCarrier.IranPostExpress,
                    code,
                    name,
                    Clean(row.Destination),
                    null,
                    BuildPostExpressCard(name, code),
                    safe,
                    safe ? null : "نام گیرنده یا اطمینان استخراج متن پست ویژه نیازمند بررسی است"));
            }

            var duplicates = items.GroupBy(value => value.TrackingCode)
                .Where(value => value.Count() > 1).Select(value => value.Key).ToArray();
            if (duplicates.Length > 0)
                return new TrackingImportParseResult(false, [],
                    $"مرحله کنترل تکراری متوقف شد: کد تکراری در متن پیدا شد ({string.Join("، ", duplicates)})؛ هیچ پیامی ارسال نشد.");
            return new TrackingImportParseResult(true, items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Iran Post Express text parsing failed.");
            return new TrackingImportParseResult(false, [],
                "مرحله پردازش متن پست ویژه با خطای فنی روبه‌رو شد؛ هیچ پیامی ارسال نشد و جزئیات در لاگ ثبت شد.");
        }
    }

    public async Task<TrackingImportParseResult> ParseIranPostPdfAsync(byte[] pdf, CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
            return new TrackingImportParseResult(false, [], "سرویس OpenAI برای تشخیص PDF فعال نیست.");
        if (pdf.Length is < 5 || pdf.Length > 20 * 1024 * 1024 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return new TrackingImportParseResult(false, [], "فایل PDF معتبر نیست یا بیشتر از ۲۰ مگابایت است.");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"tracking-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var pdfPath = Path.Combine(tempRoot, "source.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdf, ct);
            var trackingLocations = await ExtractTrackingCodeLocationsFromPdfAsync(pdfPath, tempRoot, ct);
            var expectedTrackingCodes = trackingLocations.Keys.ToArray();
            var prefix = Path.Combine(tempRoot, "page");
            var render = await RunProcessAsync("pdftoppm",
                $"-jpeg -r 150 -jpegopt quality=88,progressive=n \"{pdfPath}\" \"{prefix}\"", tempRoot, ct);
            if (render.ExitCode != 0)
            {
                _logger.LogError("PDF rendering failed: {Error}", render.Error);
                return new TrackingImportParseResult(false, [],
                    "مرحله تبدیل PDF پست به تصویر ناموفق بود؛ فایل ممکن است خراب یا رمزدار باشد. هیچ پیامی ارسال نشد.");
            }
            var pages = Directory.GetFiles(tempRoot, "page-*.jpg")
                .OrderBy(NaturalPageNumber).Take(12).ToArray();
            if (pages.Length == 0)
                return new TrackingImportParseResult(false, [],
                    "مرحله تبدیل PDF ناموفق بود: هیچ صفحه‌ای از فایل ساخته نشد؛ هیچ پیامی ارسال نشد.");
            if (Directory.GetFiles(tempRoot, "page-*.jpg").Length > 12)
                return new TrackingImportParseResult(false, [], "PDF بیش از ۱۲ صفحه است؛ آن را به چند بخش تقسیم کنید.");

            // Postal digits are safety-critical. Two agreeing reads are required, but page and
            // row metadata are deliberately not part of consensus because rows may span pages.
            // A third read is made only when one read is empty/fails or the first two disagree.
            var reads = new List<IReadOnlyCollection<PostRecognizedRow>>(3);
            (IReadOnlyCollection<PostRecognizedRow> Primary,
                IReadOnlyCollection<PostRecognizedRow> Verification)? consensus = null;
            for (var attempt = 1; attempt <= 3 && consensus is null; attempt++)
            {
                try
                {
                    var read = await RecognizePostRecipientCropsAsync(
                        pages, trackingLocations, tempRoot, ct);
                    if (read.Count > 100)
                        return new TrackingImportParseResult(false, [],
                            "حداکثر ۱۰۰ مرسوله را در هر مرحله وارد کنید.");
                    if (read.Count == 0)
                    {
                        _logger.LogWarning("Iran Post recognition attempt {Attempt} returned no rows.", attempt);
                        continue;
                    }

                    if (expectedTrackingCodes.Length > 0 &&
                        !PostReadContainsExpectedCodes(read, expectedTrackingCodes))
                    {
                        _logger.LogWarning(
                            "Iran Post recognition attempt {Attempt} did not return the deterministic PDF code set. Expected={Expected}, Actual={Actual}.",
                            attempt, expectedTrackingCodes.Length, read.Count);
                        continue;
                    }

                    reads.Add(read);
                    consensus = FindAgreeingPostReads(reads);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "Iran Post table recognition attempt {Attempt} failed.", attempt);
                }
            }

            var singleReadFallback = consensus is null && reads.Count == 1 && expectedTrackingCodes.Length > 0;
            if (consensus is null && !singleReadFallback)
            {
                if (reads.Count == 0)
                    return new TrackingImportParseResult(false, [],
                        "پس از سه بار بررسی، هیچ ردیف مرسوله‌ای در جدول اصلی PDF پیدا نشد؛ کیفیت یا ساختار فایل را بررسی کنید. هیچ پیامی ارسال نشد.");
                if (reads.Count == 1)
                    return new TrackingImportParseResult(false, [],
                        "فقط یک بار خواندن معتبر از PDF به دست آمد و کدهای قطعی از متن PDF نیز قابل استخراج نبود؛ هیچ پیامی ارسال نشد.");
                return new TrackingImportParseResult(false, [],
                    "سه بار خواندن PDF به نتیجه مشترک نرسید؛ برای جلوگیری از ارسال اشتباه، هیچ پیامی ارسال نشد.");
            }

            var recognized = singleReadFallback ? reads[0] : consensus!.Value.Primary;
            var verification = singleReadFallback ? reads[0] : consensus!.Value.Verification;
            if (singleReadFallback)
                _logger.LogWarning(
                    "Iran Post PDF continued with one valid vision read because all {Count} tracking codes were independently extracted from PDF text. Every row remains blocked for admin review.",
                    expectedTrackingCodes.Length);

            var items = new List<TrackingImportItem>();
            var verifiedRows = verification.ToDictionary(
                value => NormalizeDigits(value.TrackingCode),
                StringComparer.Ordinal);
            var orderedRows = recognized.OrderBy(value => value.Page).ThenBy(value => value.RowOrder).ToArray();
            foreach (var row in orderedRows)
            {
                var code = NormalizeDigits(row.TrackingCode);
                if (code.Length is < 15 or > 30 || string.IsNullOrWhiteSpace(row.RecipientName)) continue;
                if (!verifiedRows.TryGetValue(code, out var verifiedRow)) continue;
                if (!trackingLocations.TryGetValue(code, out var location) ||
                    location.Page < 1 || location.Page > pages.Length)
                    continue;
                try
                {
                    var recipientName = Clean(row.RecipientName);
                    var recipientCrop = await CropNormalizedAsync(
                        pages[location.Page - 1], location.RecipientBox, 0, tempRoot, ct);
                    var trackingNumberCrop = await CropNormalizedAsync(
                        pages[location.Page - 1], location.TrackingNumberBox, 0, tempRoot, ct);
                    items.Add(new TrackingImportItem(
                        TrackingCarrier.IranPost, code, recipientName, Clean(row.Destination), null,
                        await BuildPostCardAsync(recipientCrop, trackingNumberCrop, tempRoot, ct),
                        !singleReadFallback && row.Confidence >= .90 && verifiedRow.Confidence >= .90 &&
                        PostRecipientNamesAgree(row, verifiedRow),
                        singleReadFallback
                            ? "فقط یک خواندن تصویری معتبر بود؛ کد با متن PDF کنترل شد اما نام گیرنده باید با «ساخت مجدد» بازبینی شود"
                            : row.Confidence >= .90 && verifiedRow.Confidence >= .90 &&
                              PostRecipientNamesAgree(row, verifiedRow)
                            ? null
                            : $"نام گیرنده یا اطمینان خواندن نیازمند بررسی است (بار اول {row.Confidence:P0}، بازبینی {verifiedRow.Confidence:P0})"));
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "Iran Post crop/card generation failed for page {Page}, recipient page {RecipientPage}, tracking page {TrackingPage}, row {Row}, tracking {TrackingCode}.",
                        location.Page, location.Page, location.Page, row.RowOrder, code);
                    return new TrackingImportParseResult(false, [],
                        $"مرحله برش قطعی نام و عدد کد یا ساخت تصویر برای کد {code} در صفحه {location.Page} ناموفق بود؛ هیچ پیامی ارسال نشد.");
                }
            }

            var duplicates = items.GroupBy(value => value.TrackingCode).Where(value => value.Count() > 1)
                .Select(value => value.Key).ToArray();
            if (duplicates.Length > 0)
                return new TrackingImportParseResult(false, [],
                    $"PDF مبهم است و کد تکراری استخراج شد: {string.Join("، ", duplicates)}");
            if (items.Count != recognized.Count)
                return new TrackingImportParseResult(false, [],
                    $"برای ایمنی ارسال متوقف شد: {recognized.Count} ردیف تشخیص داده شد ولی فقط {items.Count} ردیف معتبر بود.");
            return new TrackingImportParseResult(true, items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Iran Post tracking PDF parsing failed.");
            return new TrackingImportParseResult(false, [],
                "مرحله پردازش فنی PDF پست با خطای پیش‌بینی‌نشده روبه‌رو شد؛ هیچ پیامی ارسال نشد و جزئیات در لاگ ثبت شد.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public async Task<TrackingMatch> MatchAsync(string recipientName, string destination, CancellationToken ct)
    {
        var target = NormalizeName(recipientName);
        if (target.Length < 2)
            return new TrackingMatch(null, null, TrackingDispatchStatus.NeedsReview, "نام گیرنده قابل تطبیق نیست");

        var active = await _db.OrderItems.AsNoTracking()
            .Include(value => value.Order)!.ThenInclude(value => value!.Customer)
            .Include(value => value.Order)!.ThenInclude(value => value!.DeliveryAddress)
            .Where(value => !value.IsDeleted && value.ShippingRequestId != null && value.Order != null &&
                value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped)
            .OrderByDescending(value => value.ShippingRequestedAt)
            .Select(value => new Candidate(
                value.Order!.CustomerId,
                value.ShippingRequestId!.Value,
                value.Order.DeliveryAddress != null ? value.Order.DeliveryAddress.ReceiverName : string.Empty,
                value.Order.DeliveryAddress != null ? value.Order.DeliveryAddress.City : string.Empty,
                value.Order.DeliveryAddress != null ? value.Order.DeliveryAddress.FullAddress : string.Empty,
                value.Order.Customer != null ? value.Order.Customer.FullName : string.Empty,
                value.ShippingRequestedAt ?? value.CreatedAt,
                true))
            .Distinct()
            .ToArrayAsync(ct);

        var activeResult = SelectUnique(target, destination, active, requireExact: false);
        if (activeResult is not null) return activeResult;

        var recentCutoff = DateTime.UtcNow.AddDays(-180);
        var recent = await _db.Addresses.AsNoTracking()
            .Where(value => !value.IsDeleted && (value.UpdatedAt ?? value.CreatedAt) >= recentCutoff)
            .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => new Candidate(value.CustomerId, null, value.ReceiverName, value.City,
                value.FullAddress, value.Customer != null ? value.Customer.FullName : string.Empty,
                value.UpdatedAt ?? value.CreatedAt, false))
            .ToArrayAsync(ct);
        var recentResult = SelectUnique(target, destination, recent, requireExact: false);
        if (recentResult is not null) return recentResult;

        var old = await _db.Addresses.AsNoTracking()
            .Where(value => !value.IsDeleted && (value.UpdatedAt ?? value.CreatedAt) < recentCutoff)
            .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => new Candidate(value.CustomerId, null, value.ReceiverName, value.City,
                value.FullAddress, value.Customer != null ? value.Customer.FullName : string.Empty,
                value.UpdatedAt ?? value.CreatedAt, false))
            .ToArrayAsync(ct);
        var oldResult = SelectUnique(target, destination, old, requireExact: true);
        return oldResult ?? new TrackingMatch(null, null, TrackingDispatchStatus.NeedsReview,
            "تطبیق یکتا پیدا نشد؛ نیازمند بررسی ادمین");
    }

    public byte[] BuildCard(
        TrackingCarrier carrier, string recipientName, string trackingCode, string? trackingUrl) =>
        carrier switch
        {
            TrackingCarrier.Chapar => BuildChaparCard(recipientName, trackingCode, trackingUrl),
            TrackingCarrier.IranPostExpress => BuildPostExpressCard(recipientName, trackingCode),
            TrackingCarrier.IranPost => throw new InvalidOperationException(
                "Iran Post cards must keep the original cropped PDF text."),
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "Unsupported tracking carrier.")
        };

    public async Task<string?> RecheckRecipientNameAsync(byte[] cardImage, CancellationToken ct)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.OpenAiApiKey) || cardImage.Length == 0)
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiModel,
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "input_text",
                                text = "فقط نام گیرنده‌ای را که داخل کادر نام این کارت رهگیری دیده می‌شود بخوان. عنوان‌ها، نام زیباشی، کد رهگیری و متن‌های قالب را وارد نکن. اگر نام واضح نیست confidence را پایین اعلام کن."
                            },
                            new
                            {
                                type = "input_image",
                                image_url = $"data:image/png;base64,{Convert.ToBase64String(cardImage)}"
                            }
                        }
                    }
                },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "tracking_recipient_recheck",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                recipientName = new { type = "string" },
                                confidence = new { type = "number" }
                            },
                            required = new[] { "recipientName", "confidence" },
                            additionalProperties = false
                        }
                    }
                }
            })
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI recipient recheck returned HTTP {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output").EnumerateArray()
            .Where(value => value.TryGetProperty("content", out _))
            .SelectMany(value => value.GetProperty("content").EnumerateArray())
            .First(value => value.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .GetProperty("text").GetString();
        var parsed = JsonSerializer.Deserialize<RecipientRecheckResponse>(outputText!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return parsed is { Confidence: >= .90 } && !string.IsNullOrWhiteSpace(parsed.RecipientName)
            ? Clean(parsed.RecipientName)
            : null;
    }

    private static TrackingMatch? SelectUnique(
        string target, string destination, IEnumerable<Candidate> source, bool requireExact)
    {
        var normalizedDestination = NormalizeName(destination);
        var ranked = source.GroupBy(value => value.CustomerId).Select(group =>
        {
            var best = group.Select(value => new
                {
                    Candidate = value,
                    Score = CandidateNameScore(target, value) +
                        (!string.IsNullOrWhiteSpace(normalizedDestination) &&
                         (NormalizeName(value.City).Contains(normalizedDestination, StringComparison.Ordinal) ||
                          NormalizeName(value.FullAddress).Contains(normalizedDestination, StringComparison.Ordinal)) ? .03 : 0)
                })
                .OrderByDescending(value => value.Score)
                .ThenByDescending(value => value.Candidate.Date)
                .First();
            return best;
        }).OrderByDescending(value => value.Score).ThenByDescending(value => value.Candidate.Date).ToArray();
        if (ranked.Length == 0) return null;
        var threshold = requireExact ? .999 : .87;
        if (ranked[0].Score < threshold) return null;
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < .08) return null;
        var candidate = ranked[0].Candidate;
        var note = candidate.IsActive
            ? "تطبیق با درخواست پست فعال و آدرس اخیر"
            : requireExact ? "تطبیق دقیق با آدرس قدیمی" : "تطبیق با آدرس ثبت‌شده اخیر";
        return new TrackingMatch(candidate.CustomerId, candidate.ShippingRequestId,
            TrackingDispatchStatus.Ready, note);
    }

    private async Task<IReadOnlyCollection<PostRecognizedRow>> RecognizePostRowsAsync(
        IReadOnlyCollection<string> pagePaths,
        IReadOnlyCollection<string> expectedTrackingCodes,
        CancellationToken ct)
    {
        var deterministicCodeInstruction = expectedTrackingCodes.Count == 0
            ? ""
            : $"""

               کدهای رهگیری زیر مستقیماً و بدون OCR از متن خود PDF استخراج شده‌اند و فهرست قطعی جدول اصلی هستند. خروجی باید برای هر کد دقیقاً یک ردیف و در مجموع دقیقاً {expectedTrackingCodes.Count} ردیف داشته باشد. کد دیگری اضافه نکن و هیچ‌کدام را حذف نکن. اگر همان کد را در جدول بیمه دیدی، آن را نادیده بگیر و فقط ردیف جدول اصلی را که نام گیرنده دارد استفاده کن:
               {string.Join("\n", expectedTrackingCodes)}
               """;
        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = """
                    این صفحات خروجی رسید انبوه پست ایران هستند. فقط ردیف‌های جدول اصلی مرسولات را بخوان؛ جدول بیمه یا جدول‌های تکرارشده در صفحات بعدی را کاملاً نادیده بگیر. برای هر ردیف، نام گیرنده و کد رهگیری همان ردیف را استخراج کن. ممکن است یک ردیف در مرز دو صفحه شکسته شده باشد؛ مثلاً کد در انتهای یک صفحه و نام گیرنده در ابتدای صفحه بعد باشد. در این حالت دو بخش را یک مرسوله واحد در نظر بگیر، page را صفحه شروع ردیف، trackingPage را صفحه کد و recipientPage را صفحه نام قرار بده. هرگز دو بخش یک ردیف شکسته را دو مرسوله جدا حساب نکن. مختصات نام گیرنده را در recipientBox بده. در trackingBox کل سلول شماره مرسوله شامل بارکد کامل و عدد زیر بارکد را بده، اما ستون ردیف کناری را وارد نکن. همه مختصات به صورت [x1,y1,x2,y2] در مقیاس صفر تا 1000 باشند. ستون فرستنده و نام «زیباشی» نباید داخل recipientBox باشد. کد را با رقم لاتین برگردان، اما تصویر نهایی از روی همان نوشته اصلی PDF بریده خواهد شد. rowOrder ترتیب منطقی ردیف‌ها از بالا به پایین است. اگر درباره نام ردیفی مطمئن نیستی آن را حذف نکن و confidence را پایین‌تر بده.
                    """ + deterministicCodeInstruction
            }
        };
        var pageNumber = 0;
        foreach (var path in pagePaths)
        {
            pageNumber++;
            content.Add(new { type = "input_text", text = $"صفحه {pageNumber}" });
            content.Add(new
            {
                type = "input_image",
                image_url = $"data:image/jpeg;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct))}"
            });
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiModel,
                input = new[] { new { role = "user", content = content.ToArray() } },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "iran_post_rows",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                rows = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            page = new { type = "integer" },
                                            recipientPage = new { type = "integer" },
                                            trackingPage = new { type = "integer" },
                                            rowOrder = new { type = "integer" },
                                            recipientName = new { type = "string" },
                                            trackingCode = new { type = "string" },
                                            destination = new { type = "string" },
                                            recipientBox = new { type = "array", items = new { type = "integer" } },
                                            trackingBox = new { type = "array", items = new { type = "integer" } },
                                            confidence = new { type = "number" }
                                        },
                                        required = new[] { "page", "recipientPage", "trackingPage", "rowOrder", "recipientName", "trackingCode", "destination", "recipientBox", "trackingBox", "confidence" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "rows" },
                            additionalProperties = false
                        }
                    }
                }
            })
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI returned HTTP {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output").EnumerateArray()
            .Where(value => value.TryGetProperty("content", out _))
            .SelectMany(value => value.GetProperty("content").EnumerateArray())
            .First(value => value.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .GetProperty("text").GetString();
        var parsed = JsonSerializer.Deserialize<PostRowsResponse>(outputText!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("OpenAI returned an empty PDF result.");
        if (parsed.Rows.Any(value => value.RecipientBox.Length != 4 || value.TrackingBox.Length != 4 ||
                                     value.RecipientBox.Any(coordinate => coordinate is < 0 or > 1000) ||
                                     value.TrackingBox.Any(coordinate => coordinate is < 0 or > 1000)))
            throw new InvalidOperationException("One or more postal rows have invalid crop coordinates.");
        return parsed.Rows;
    }

    private async Task<IReadOnlyCollection<PostRecognizedRow>> RecognizePostRecipientCropsAsync(
        IReadOnlyList<string> pagePaths,
        IReadOnlyDictionary<string, PdfTrackingLocation> trackingLocations,
        string tempRoot,
        CancellationToken ct)
    {
        var ordered = trackingLocations
            .OrderBy(value => value.Value.Page)
            .ThenBy(value => value.Value.TrackingNumberBox[1])
            .ToArray();
        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = $"""
                    این تصاویر فقط برش ستون نام گیرنده از {ordered.Length} ردیف قطعی جدول اصلی پست ایران هستند. قبل از هر تصویر، کد رهگیری قطعی همان ردیف نوشته شده است. فقط نام فارسی گیرنده داخل تصویر را بخوان و همان کد را بدون هیچ تغییری در خروجی برگردان. عنوان، خط جدول، نام فرستنده یا متن دیگری اضافه نکن. برای هر کد دقیقاً یک خروجی بده. اگر نام واضح نیست رشته خالی و confidence پایین بده؛ هرگز نام ردیف دیگری را حدس نزن.
                    """
            }
        };
        foreach (var entry in ordered)
        {
            var location = entry.Value;
            if (location.Page < 1 || location.Page > pagePaths.Count)
                continue;
            var crop = await CropNormalizedAsync(
                pagePaths[location.Page - 1], location.RecipientBox, 0, tempRoot, ct);
            content.Add(new
            {
                type = "input_text",
                text = $"کد رهگیری قطعی این تصویر: {entry.Key}"
            });
            content.Add(new
            {
                type = "input_image",
                image_url = $"data:image/png;base64,{Convert.ToBase64String(crop)}"
            });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiModel,
                input = new[] { new { role = "user", content = content.ToArray() } },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "iran_post_recipient_crops",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                rows = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            trackingCode = new { type = "string" },
                                            recipientName = new { type = "string" },
                                            confidence = new { type = "number" }
                                        },
                                        required = new[] { "trackingCode", "recipientName", "confidence" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "rows" },
                            additionalProperties = false
                        }
                    }
                }
            })
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI recipient-crop recognition returned HTTP {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output").EnumerateArray()
            .Where(value => value.TryGetProperty("content", out _))
            .SelectMany(value => value.GetProperty("content").EnumerateArray())
            .First(value => value.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .GetProperty("text").GetString();
        var parsed = JsonSerializer.Deserialize<PostRecipientRowsResponse>(outputText!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("OpenAI returned an empty recipient-crop result.");

        var rowsByCode = parsed.Rows
            .GroupBy(value => NormalizeDigits(value.TrackingCode), StringComparer.Ordinal)
            .Where(value => value.Count() == 1)
            .ToDictionary(value => value.Key, value => value.Single(), StringComparer.Ordinal);
        var result = new List<PostRecognizedRow>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            if (!rowsByCode.TryGetValue(entry.Key, out var row))
                continue;
            var location = entry.Value;
            var recipientName = string.IsNullOrWhiteSpace(row.RecipientName)
                ? "نام گیرنده خوانده نشد"
                : Clean(row.RecipientName);
            result.Add(new PostRecognizedRow(
                location.Page, location.Page, location.Page, index + 1,
                recipientName, entry.Key, string.Empty,
                location.RecipientBox, location.TrackingNumberBox,
                string.IsNullOrWhiteSpace(row.RecipientName) ? 0 : row.Confidence));
        }
        return result;
    }

    private async Task<PostExpressRow[]> RecognizePostExpressRowsAsync(string sourceText, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiModel,
                input = new object[]
                {
                    new
                    {
                        role = "system",
                        content = """
                            متن اعلان یک یا چند مرسوله پست ویژه ایران را بخوان. برای هر مرسوله فقط نام گیرنده، کد رهگیری و مقصد را استخراج کن. ارقام کد رهگیری و املای نام گیرنده را دقیقاً مطابق متن ورودی حفظ کن و هیچ چیزی را حدس نزن. شماره موبایل، کدپستی، مبلغ و شماره سفارش کد رهگیری نیستند. اگر نام یا کد مبهم است confidence را کمتر از 0.90 برگردان.
                            """
                    },
                    new { role = "user", content = sourceText }
                },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "iran_post_express_rows",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                rows = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            recipientName = new { type = "string" },
                                            trackingCode = new { type = "string" },
                                            destination = new { type = "string" },
                                            confidence = new { type = "number" }
                                        },
                                        required = new[] { "recipientName", "trackingCode", "destination", "confidence" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "rows" },
                            additionalProperties = false
                        }
                    }
                }
            })
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI returned HTTP {(int)response.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output").EnumerateArray()
            .Where(value => value.TryGetProperty("content", out _))
            .SelectMany(value => value.GetProperty("content").EnumerateArray())
            .First(value => value.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .GetProperty("text").GetString();
        return JsonSerializer.Deserialize<PostExpressRowsResponse>(outputText!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })?.Rows ?? [];
    }

    private static async Task<byte[]> CropNormalizedAsync(
        string imagePath, int[] box, int padding, string tempRoot, CancellationToken ct)
    {
        var dimensions = await RunProcessAsync("identify", $"-format \"%w %h\" \"{imagePath}\"", tempRoot, ct);
        if (dimensions.ExitCode != 0)
            throw new InvalidOperationException($"Image identify failed: {dimensions.Error}");
        var parts = dimensions.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            throw new InvalidOperationException("Cannot read rendered PDF page dimensions.");
        var left = Math.Clamp((int)Math.Floor(box[0] / 1000d * width) - padding, 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(box[1] / 1000d * height) - padding, 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling(box[2] / 1000d * width) + padding, left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(box[3] / 1000d * height) + padding, top + 1, height);
        var output = Path.Combine(tempRoot, $"crop-{Guid.NewGuid():N}.png");
        var crop = await RunProcessAsync("convert",
            $"\"{imagePath}\" -crop {right - left}x{bottom - top}+{left}+{top} +repage \"{output}\"",
            tempRoot, ct);
        if (crop.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"Image crop failed: {crop.Error}");
        return await File.ReadAllBytesAsync(output, ct);
    }

    private async Task<byte[]> BuildPostCardAsync(
        byte[] recipientCrop, byte[] trackingNumberCrop, string tempRoot, CancellationToken ct)
    {
        var template = Path.Combine(
            _environment.ContentRootPath, "Assets", "Tracking", "iran-post-template.jpg");
        if (!File.Exists(template))
            throw new FileNotFoundException("Iran Post tracking template is missing.", template);

        var recipientPath = Path.Combine(tempRoot, $"recipient-{Guid.NewGuid():N}.png");
        var trackingPath = Path.Combine(tempRoot, $"tracking-{Guid.NewGuid():N}.png");
        var recipientLayer = Path.Combine(tempRoot, $"recipient-layer-{Guid.NewGuid():N}.png");
        var trackingLayer = Path.Combine(tempRoot, $"tracking-layer-{Guid.NewGuid():N}.png");
        var output = Path.Combine(tempRoot, $"post-card-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(recipientPath, recipientCrop, ct);
        await File.WriteAllBytesAsync(trackingPath, trackingNumberCrop, ct);

        var nameLayerResult = await RunProcessAsync("convert",
            $"\"{recipientPath}\" -colorspace Gray -contrast-stretch 1%x1% -morphology Dilate Octagon:1 " +
            "-fuzz 14% -transparent white -trim +repage " +
            "-resize \"620x140\" -gravity center -background none -extent 630x145 " +
            $"\"{recipientLayer}\"", tempRoot, ct);
        if (nameLayerResult.ExitCode != 0)
            throw new InvalidOperationException($"Recipient layer generation failed: {nameLayerResult.Error}");

        var codeLayerResult = await RunProcessAsync("convert",
            $"\"{trackingPath}\" -colorspace Gray -contrast-stretch 1%x1% -fuzz 14% -transparent white -trim +repage " +
            "-resize \"660x135\" -gravity center -background none -extent 670x140 " +
            $"\"{trackingLayer}\"", tempRoot, ct);
        if (codeLayerResult.ExitCode != 0)
            throw new InvalidOperationException($"Tracking-code layer generation failed: {codeLayerResult.Error}");

        var cardResult = await RunProcessAsync("convert",
            $"\"{template}\" \"{recipientLayer}\" -geometry +478+408 -composite " +
            $"\"{trackingLayer}\" -geometry +448+699 -composite \"{output}\"",
            tempRoot, ct);
        if (cardResult.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"Iran Post card composition failed: {cardResult.Error}");
        return await File.ReadAllBytesAsync(output, ct);
    }

    private byte[] BuildPostExpressCard(string name, string code)
    {
        var template = Path.Combine(
            _environment.ContentRootPath, "Assets", "Tracking", "iran-post-template.jpg");
        if (!File.Exists(template))
            throw new FileNotFoundException("Iran Post tracking template is missing.", template);
        var templateBytes = File.ReadAllBytes(template);
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(120, 120, Unit.Millimetre);
            page.Margin(0);
            page.Background().Image(templateBytes).FitArea();
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).Bold());
            page.ContentFromRightToLeft();
            page.Content().Column(column =>
            {
                column.Item().Height(37, Unit.Millimetre);
                column.Item().Height(16, Unit.Millimetre).PaddingLeft(44, Unit.Millimetre)
                    .PaddingRight(14, Unit.Millimetre).AlignCenter().AlignMiddle()
                    .Text(name).FontSize(name.Length > 28 ? 18 : 23).ExtraBold()
                    .FontColor(Colors.Grey.Darken4);
                column.Item().Height(11, Unit.Millimetre);
                column.Item().Height(15, Unit.Millimetre).PaddingLeft(41, Unit.Millimetre)
                    .PaddingRight(14, Unit.Millimetre).ContentFromLeftToRight()
                    .AlignCenter().AlignMiddle().Text(code).FontSize(code.Length > 20 ? 18 : 22).ExtraBold()
                    .FontColor(Colors.Grey.Darken4);
            });
        }));
        return document.GenerateImages(new ImageGenerationSettings
        {
            ImageFormat = ImageFormat.Png,
            RasterDpi = 271
        }).Single();
    }

    private byte[] BuildChaparCard(string name, string code, string? url)
    {
        var template = Path.Combine(
            _environment.ContentRootPath, "Assets", "Tracking", "chapar-template.jpg");
        if (!File.Exists(template))
            throw new FileNotFoundException("Chapar tracking template is missing.", template);
        var templateBytes = File.ReadAllBytes(template);
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(120, 120, Unit.Millimetre);
            page.Margin(0);
            page.Background().Image(templateBytes).FitArea();
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).Bold());
            page.ContentFromRightToLeft();
            page.Content().Column(column =>
            {
                column.Item().Height(53, Unit.Millimetre);
                column.Item().Height(10, Unit.Millimetre).PaddingHorizontal(20, Unit.Millimetre)
                    .AlignCenter().AlignMiddle().Text(name).FontSize(name.Length > 28 ? 18 : 23).ExtraBold()
                    .FontColor(Colors.Grey.Darken4);
                column.Item().Height(3, Unit.Millimetre);
                column.Item().Height(12, Unit.Millimetre).PaddingHorizontal(23, Unit.Millimetre)
                    .ContentFromLeftToRight().AlignCenter().AlignMiddle().Text(code)
                    .FontSize(code.Length > 20 ? 18 : 22).ExtraBold().FontColor(Colors.Grey.Darken4);
            });
        }));
        return document.GenerateImages(new ImageGenerationSettings
        {
            ImageFormat = ImageFormat.Png,
            RasterDpi = 271
        }).Single();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static int NaturalPageNumber(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    private static string Clean(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private sealed record RecipientRecheckResponse(string RecipientName, double Confidence);
    private static string NormalizeDigits(string value) => new(value.Select(character => character switch
    {
        >= '\u06F0' and <= '\u06F9' => (char)('0' + character - '\u06F0'),
        >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
        _ => character
    }).Where(char.IsAsciiDigit).ToArray());

    private static string NormalizeName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک')
            .Replace("‌", " ");
        return Regex.Replace(normalized, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();
    }

    private static double NameScore(string left, string right)
    {
        if (left == right && left.Length > 0) return 1;
        if (left.Length < 3 || right.Length < 3) return 0;
        var distance = Levenshtein(left, right);
        return 1d - distance / (double)Math.Max(left.Length, right.Length);
    }

    private static double CandidateNameScore(string target, Candidate candidate)
    {
        var receiverScore = NameScore(target, NormalizeName(candidate.ReceiverName));
        var customerScore = NameScore(target, NormalizeName(candidate.CustomerFullName));
        var normalizedRawAddress = NormalizeName(candidate.FullAddress);
        var rawAddressScore = target.Length >= 5 &&
                              normalizedRawAddress.Contains(target, StringComparison.Ordinal)
            ? 1d
            : 0d;
        return Math.Max(receiverScore, Math.Max(customerScore, rawAddressScore));
    }

    private static async Task<IReadOnlyDictionary<string, PdfTrackingLocation>> ExtractTrackingCodeLocationsFromPdfAsync(
        string pdfPath,
        string tempRoot,
        CancellationToken ct)
    {
        var textPath = Path.Combine(tempRoot, "source-bbox.html");
        var extraction = await RunProcessAsync("pdftotext",
            $"-bbox -enc UTF-8 \"{pdfPath}\" \"{textPath}\"", tempRoot, ct);
        if (extraction.ExitCode != 0 || !File.Exists(textPath))
            return new Dictionary<string, PdfTrackingLocation>(StringComparer.Ordinal);

        await using var stream = File.OpenRead(textPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var candidates = new Dictionary<string, (double X, PdfTrackingLocation Location)>(StringComparer.Ordinal);
        var pageNumber = 0;
        foreach (var page in document.Descendants().Where(value => value.Name.LocalName == "page"))
        {
            pageNumber++;
            if (!TryReadDouble(page, "width", out var width) ||
                !TryReadDouble(page, "height", out var height) || width <= 0 || height <= 0)
                continue;
            foreach (var word in page.Descendants().Where(value => value.Name.LocalName == "word"))
            {
                var code = NormalizeDigits(word.Value);
                if (code.Length is < 20 or > 30 || !code.All(char.IsDigit) ||
                    !TryReadDouble(word, "xMin", out var xMin) ||
                    !TryReadDouble(word, "xMax", out var xMax) ||
                    !TryReadDouble(word, "yMin", out var yMin) ||
                    !TryReadDouble(word, "yMax", out var yMax))
                    continue;

                var normalizedX = xMin / width * 1000d;
                var numberTop = Math.Clamp((int)Math.Floor(yMin / height * 1000d) - 1, 0, 998);
                var numberBottom = Math.Clamp((int)Math.Ceiling(yMax / height * 1000d) + 1,
                    numberTop + 1, 1000);
                var numberLeft = Math.Clamp((int)Math.Floor(xMin / width * 1000d) - 1, 0, 998);
                var numberRight = Math.Clamp((int)Math.Ceiling(xMax / width * 1000d) + 1,
                    numberLeft + 1, 1000);
                var location = new PdfTrackingLocation(
                    pageNumber,
                    [numberLeft, numberTop, numberRight, numberBottom],
                    [626, Math.Clamp(numberTop - 35, 0, 998), 718,
                        Math.Clamp(numberTop - 5, 1, 1000)]);
                // Every code is repeated in the insurance table. The shipment table is the
                // right-most occurrence, so select it deterministically without relying on a
                // fixed page or row number.
                if (!candidates.TryGetValue(code, out var current) || normalizedX > current.X)
                    candidates[code] = (normalizedX, location);
            }
        }
        var result = candidates.ToDictionary(
            value => value.Key, value => value.Value.Location, StringComparer.Ordinal);
        return result.Count <= 100
            ? result
            : result.Take(100).ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
    }

    private static bool TryReadDouble(XElement element, string attributeName, out double value) =>
        double.TryParse(element.Attribute(attributeName)?.Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);

    private static bool PostReadContainsExpectedCodes(
        IReadOnlyCollection<PostRecognizedRow> read,
        IReadOnlyCollection<string> expectedTrackingCodes)
    {
        var actual = read.Select(value => NormalizeDigits(value.TrackingCode)).ToArray();
        return actual.Length == expectedTrackingCodes.Count &&
               actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
               actual.ToHashSet(StringComparer.Ordinal)
                   .SetEquals(expectedTrackingCodes);
    }

    private static bool PostRecipientNamesAgree(PostRecognizedRow first, PostRecognizedRow second) =>
        NameScore(NormalizeName(first.RecipientName), NormalizeName(second.RecipientName)) >= .90;

    private static bool PostReadsAgree(
        IReadOnlyCollection<PostRecognizedRow> first,
        IReadOnlyCollection<PostRecognizedRow> second)
    {
        if (first.Count != second.Count) return false;
        var left = first
            .GroupBy(value => NormalizeDigits(value.TrackingCode), StringComparer.Ordinal)
            .ToArray();
        var right = second
            .GroupBy(value => NormalizeDigits(value.TrackingCode), StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.Ordinal);
        if (left.Any(value => string.IsNullOrWhiteSpace(value.Key) || value.Count() != 1) ||
            right.Any(value => string.IsNullOrWhiteSpace(value.Key) || value.Value.Length != 1))
            return false;
        foreach (var group in left)
        {
            if (!right.TryGetValue(group.Key, out _))
                return false;
        }
        return true;
    }

    private static (IReadOnlyCollection<PostRecognizedRow> Primary,
        IReadOnlyCollection<PostRecognizedRow> Verification)? FindAgreeingPostReads(
        IReadOnlyList<IReadOnlyCollection<PostRecognizedRow>> reads)
    {
        for (var left = 0; left < reads.Count; left++)
        for (var right = left + 1; right < reads.Count; right++)
            if (PostReadsAgree(reads[left], reads[right]))
                return (reads[left], reads[right]);
        return null;
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    public void Dispose() => _httpClient.Dispose();

    [GeneratedRegex(@"مرسوله\s+چاپار\s*[:：].*?(?=\s*مرسوله\s+چاپار\s*[:：]|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ChaparBlockPattern();

    [GeneratedRegex(@"مرسوله\s+چاپار\s*[:：]\s*(?<value>[۰-۹٠-٩0-9\s-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChaparCodePattern();

    [GeneratedRegex(@"\sبه\s*[:：]\s*(?<value>.*?)\s+گیرنده\s*[:：]", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ChaparDestinationPattern();

    [GeneratedRegex(@"گیرنده\s*[:：]\s*(?<value>.*?)(?=\s+جمع\s+کل\s*[:：])", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ChaparNamePattern();

    [GeneratedRegex(@"https?://krch\.ir/[^\s\]\)]+", RegexOptions.IgnoreCase)]
    private static partial Regex ChaparUrlPattern();

    private sealed record Candidate(Guid CustomerId, Guid? ShippingRequestId, string ReceiverName,
        string City, string FullAddress, string CustomerFullName, DateTime Date, bool IsActive);
    private sealed record PostRowsResponse(PostRecognizedRow[] Rows);
    private sealed record PostRecipientRowsResponse(PostRecipientRow[] Rows);
    private sealed record PostRecipientRow(string TrackingCode, string RecipientName, double Confidence);
    private sealed record PostExpressRowsResponse(PostExpressRow[] Rows);
    private sealed record PostExpressRow(
        string RecipientName, string TrackingCode, string Destination, double Confidence);
    private sealed record PdfTrackingLocation(
        int Page, int[] TrackingNumberBox, int[] RecipientBox);
    private sealed record PostRecognizedRow(int Page, int RecipientPage, int TrackingPage, int RowOrder, string RecipientName,
        string TrackingCode, string Destination, int[] RecipientBox, int[] TrackingBox, double Confidence);
}
