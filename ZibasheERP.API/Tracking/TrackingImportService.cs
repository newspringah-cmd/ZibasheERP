using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    byte[] CardImage);

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
    Task<TrackingImportParseResult> ParseIranPostPdfAsync(byte[] pdf, CancellationToken ct);
    Task<TrackingMatch> MatchAsync(string recipientName, string destination, CancellationToken ct);
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

            IReadOnlyCollection<PostRecognizedRow> recognized;
            try
            {
                recognized = await RecognizePostRowsAsync(pages, ct);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Primary Iran Post table recognition failed.");
                return new TrackingImportParseResult(false, [],
                    "مرحله خواندن جدول اصلی PDF پست ناموفق بود؛ اتصال API یا کیفیت فایل را بررسی کنید. هیچ پیامی ارسال نشد.");
            }
            if (recognized.Count == 0)
                return new TrackingImportParseResult(false, [], "هیچ ردیف مرسوله‌ای در جدول اصلی PDF پیدا نشد.");
            if (recognized.Count > 100)
                return new TrackingImportParseResult(false, [], "حداکثر ۱۰۰ مرسوله را در هر مرحله وارد کنید.");

            // A second independent read is intentional: postal digits are safety-critical and
            // a single OCR/model pass must never be enough to authorize delivery.
            IReadOnlyCollection<PostRecognizedRow> verification;
            try
            {
                verification = await RecognizePostRowsAsync(pages, ct);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Secondary Iran Post verification failed.");
                return new TrackingImportParseResult(false, [],
                    "مرحله بازبینی دوم PDF پست ناموفق بود؛ برای ایمنی هیچ پیامی ارسال نشد.");
            }
            if (!PostReadsAgree(recognized, verification))
                return new TrackingImportParseResult(false, [],
                    "دو بار خواندن PDF نتیجه یکسان نداشت؛ برای جلوگیری از ارسال اشتباه، کل سری متوقف شد.");

            var items = new List<TrackingImportItem>();
            foreach (var row in recognized.OrderBy(value => value.Page).ThenBy(value => value.RowOrder))
            {
                if (row.Page < 1 || row.Page > pages.Length) continue;
                var code = NormalizeDigits(row.TrackingCode);
                if (code.Length is < 15 or > 30 || string.IsNullOrWhiteSpace(row.RecipientName)) continue;
                try
                {
                    var recipientCrop = await CropNormalizedAsync(pages[row.Page - 1], row.RecipientBox, 12, tempRoot, ct);
                    var trackingCrop = await CropNormalizedAsync(pages[row.Page - 1], row.TrackingBox, 12, tempRoot, ct);
                    items.Add(new TrackingImportItem(
                        TrackingCarrier.IranPost, code, Clean(row.RecipientName), Clean(row.Destination), null,
                        BuildPostCard(recipientCrop, trackingCrop)));
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "Iran Post crop/card generation failed for page {Page}, row {Row}, tracking {TrackingCode}.",
                        row.Page, row.RowOrder, code);
                    return new TrackingImportParseResult(false, [],
                        $"مرحله برش نام و کد یا ساخت تصویر برای ردیف {row.RowOrder} صفحه {row.Page} ناموفق بود؛ هیچ پیامی ارسال نشد.");
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
                value.UpdatedAt ?? value.CreatedAt, false))
            .ToArrayAsync(ct);
        var recentResult = SelectUnique(target, destination, recent, requireExact: false);
        if (recentResult is not null) return recentResult;

        var old = await _db.Addresses.AsNoTracking()
            .Where(value => !value.IsDeleted && (value.UpdatedAt ?? value.CreatedAt) < recentCutoff)
            .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .Select(value => new Candidate(value.CustomerId, null, value.ReceiverName, value.City,
                value.UpdatedAt ?? value.CreatedAt, false))
            .ToArrayAsync(ct);
        var oldResult = SelectUnique(target, destination, old, requireExact: true);
        return oldResult ?? new TrackingMatch(null, null, TrackingDispatchStatus.NeedsReview,
            "تطبیق یکتا پیدا نشد؛ نیازمند بررسی ادمین");
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
                    Score = NameScore(target, NormalizeName(value.ReceiverName)) +
                        (!string.IsNullOrWhiteSpace(normalizedDestination) &&
                         NormalizeName(value.City).Contains(normalizedDestination, StringComparison.Ordinal) ? .03 : 0)
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
        IReadOnlyCollection<string> pagePaths, CancellationToken ct)
    {
        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = """
                    این صفحات خروجی رسید انبوه پست ایران هستند. فقط ردیف‌های جدول اصلی مرسولات را بخوان؛ جدول بیمه یا جدول‌های تکرارشده در صفحات بعدی را کاملاً نادیده بگیر. برای هر ردیف، نام گیرنده و کد رهگیری همان ردیف را استخراج کن. مختصات سلول نام گیرنده و سلول کد رهگیری را به صورت [x1,y1,x2,y2] در مقیاس صفر تا 1000 نسبت به کل همان صفحه بده تا خود تصویر اصلی بریده شود. کد را با رقم لاتین برگردان، اما تصویر نهایی از روی همان سلول PDF بریده خواهد شد. rowOrder ترتیب ردیف در صفحه از بالا به پایین است. اگر درباره ردیفی مطمئن نیستی آن را حذف نکن؛ confidence را کمتر از 0.96 بده.
                    """
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
                                            rowOrder = new { type = "integer" },
                                            recipientName = new { type = "string" },
                                            trackingCode = new { type = "string" },
                                            destination = new { type = "string" },
                                            recipientBox = new { type = "array", items = new { type = "integer" } },
                                            trackingBox = new { type = "array", items = new { type = "integer" } },
                                            confidence = new { type = "number" }
                                        },
                                        required = new[] { "page", "rowOrder", "recipientName", "trackingCode", "destination", "recipientBox", "trackingBox", "confidence" },
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
        if (parsed.Rows.Any(value => value.Confidence < .96 || value.RecipientBox.Length != 4 || value.TrackingBox.Length != 4))
            throw new InvalidOperationException("One or more postal rows were recognized with insufficient confidence.");
        return parsed.Rows;
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

    private static byte[] BuildPostCard(byte[] recipientCrop, byte[] trackingCrop) =>
        BuildCard(Colors.Red.Darken2, "کد رهگیری مرسوله", recipientCrop, trackingCrop, null, false);

    private static byte[] BuildChaparCard(string name, string code, string? url)
    {
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(120, 120, Unit.Millimetre);
            page.Margin(7, Unit.Millimetre);
            page.PageColor(Colors.Yellow.Lighten3);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).Bold().FontSize(18));
            page.ContentFromRightToLeft();
            page.Content().Background(Colors.White).Padding(8, Unit.Millimetre).Column(column =>
            {
                column.Spacing(8);
                column.Item().AlignCenter().Text("کد رهگیری مرسوله").FontSize(28);
                column.Item().AlignCenter().Text("ارسال با چاپار").FontColor(Colors.DeepPurple.Medium);
                column.Item().AlignCenter().Text(name).FontSize(25);
                column.Item().Border(1).BorderColor(Colors.Yellow.Darken2).Padding(12).AlignCenter()
                    .Text(code).FontFamily("Arial").FontSize(22);
                column.Item().AlignCenter().Text("✅ ارسال شد").FontColor(Colors.Green.Darken2).FontSize(24);
                if (!string.IsNullOrWhiteSpace(url))
                    column.Item().AlignCenter().Text(url).FontFamily("Arial").FontSize(12).FontColor(Colors.Blue.Medium);
            });
        }));
        return document.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 150 }).Single();
    }

    private static byte[] BuildCard(string color, string title, byte[] name, byte[] code, string? footer, bool unused)
    {
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(120, 120, Unit.Millimetre);
            page.Margin(7, Unit.Millimetre);
            page.PageColor(color);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).Bold().FontSize(18));
            page.ContentFromRightToLeft();
            page.Content().Background(Colors.White).Padding(8, Unit.Millimetre).Column(column =>
            {
                column.Spacing(8);
                column.Item().AlignCenter().Text(title).FontSize(28).FontColor(color);
                column.Item().Text("گیرنده").FontColor(color);
                column.Item().Height(18, Unit.Millimetre).Border(1).BorderColor(Colors.Grey.Lighten1)
                    .Padding(3).AlignCenter().AlignMiddle().Image(name).FitArea();
                column.Item().Text("شماره مرسوله").FontColor(color);
                column.Item().Height(20, Unit.Millimetre).Border(1.5f).BorderColor(color)
                    .Padding(3).AlignCenter().AlignMiddle().Image(code).FitArea();
                column.Item().AlignCenter().Text("✅ ارسال شد").FontColor(color).FontSize(25);
                column.Item().AlignCenter().Text("از خرید شما سپاسگزاریم").FontColor(Colors.Grey.Darken1);
            });
        }));
        return document.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 150 }).Single();
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

    private static bool PostReadsAgree(
        IReadOnlyCollection<PostRecognizedRow> first,
        IReadOnlyCollection<PostRecognizedRow> second)
    {
        if (first.Count != second.Count) return false;
        var left = first.OrderBy(value => value.Page).ThenBy(value => value.RowOrder).ToArray();
        var right = second.OrderBy(value => value.Page).ThenBy(value => value.RowOrder).ToArray();
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].Page != right[index].Page ||
                NormalizeDigits(left[index].TrackingCode) != NormalizeDigits(right[index].TrackingCode) ||
                NameScore(NormalizeName(left[index].RecipientName), NormalizeName(right[index].RecipientName)) < .90)
                return false;
        }
        return true;
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
        string City, DateTime Date, bool IsActive);
    private sealed record PostRowsResponse(PostRecognizedRow[] Rows);
    private sealed record PostRecognizedRow(int Page, int RowOrder, string RecipientName,
        string TrackingCode, string Destination, int[] RecipientBox, int[] TrackingBox, double Confidence);
}
