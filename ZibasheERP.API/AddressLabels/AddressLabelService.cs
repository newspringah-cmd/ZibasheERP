using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Drawing;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.AddressLabels;

public sealed record ParsedPostalAddress(
    string ReceiverName,
    string FullAddress,
    string Mobile,
    string PostalCode,
    string City);

public sealed record AddressLabelResult(
    bool IsSuccessful,
    byte[]? Pdf = null,
    byte[]? Preview = null,
    string? Error = null);

public interface IAddressLabelService
{
    bool IsEnabled { get; }
    Task<AddressLabelResult> CreateAsync(Address address, CancellationToken cancellationToken);
}

public sealed class AddressLabelService : IAddressLabelService, IDisposable
{
    private const string FontFamily = "B Nazanin";
    private readonly AddressLabelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AddressLabelService> _logger;

    public AddressLabelService(
        IOptions<AddressLabelOptions> options,
        IWebHostEnvironment environment,
        ILogger<AddressLabelService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);

        QuestPDF.Settings.License = LicenseType.Community;
        var fontPath = Path.Combine(environment.ContentRootPath, "Assets", "Fonts", "B-NAZANIN.TTF");
        using var font = File.OpenRead(fontPath);
        FontManager.RegisterFontWithCustomName(FontFamily, font);
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<AddressLabelResult> CreateAsync(Address address, CancellationToken cancellationToken)
    {
        try
        {
            var isRawAccountantAddress =
                string.Equals(address.Description, "آدرس خام ثبت‌شده توسط حسابدار", StringComparison.Ordinal);
            var parseFromCombinedText = isRawAccountantAddress ||
                string.IsNullOrWhiteSpace(address.ReceiverName) ||
                string.IsNullOrWhiteSpace(address.Mobile) ||
                string.IsNullOrWhiteSpace(address.City);
            var parsed = parseFromCombinedText
                ? await ParseRawAddressAsync(
                    isRawAccountantAddress ? address.FullAddress : BuildRawAddress(address),
                    cancellationToken)
                : new ParsedPostalAddress(
                    address.ReceiverName.Trim(), address.FullAddress.Trim(), address.Mobile.Trim(),
                    address.PostalCode.Trim(), address.City.Trim());

            var missing = MissingFields(parsed);
            if (missing.Length > 0)
                return new AddressLabelResult(false, Error:
                    $"اطلاعات {string.Join("، ", missing)} در متن آدرس پیدا نشد؛ آدرس را کامل‌تر ثبت کنید.");

            parsed = parsed with { PostalCode = NormalizePostalCode(parsed.PostalCode) };
            var document = BuildDocument(parsed);
            var pdf = document.GeneratePdf();
            var preview = document.GenerateImages(new ImageGenerationSettings
            {
                ImageFormat = ImageFormat.Jpeg,
                ImageCompressionQuality = ImageCompressionQuality.VeryHigh,
                RasterDpi = 200
            }).Single();
            return new AddressLabelResult(true, pdf, preview);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Address label generation failed.");
            return new AddressLabelResult(false, Error: "ساخت لیبل با خطا روبه‌رو شد؛ جزئیات در لاگ ثبت شد.");
        }
    }

    private static string BuildRawAddress(Address address) => string.Join("\n", new[]
    {
        string.IsNullOrWhiteSpace(address.ReceiverName) ? null : $"نام گیرنده: {address.ReceiverName}",
        string.IsNullOrWhiteSpace(address.Mobile) ? null : $"موبایل: {address.Mobile}",
        string.IsNullOrWhiteSpace(address.PostalCode) ? null : $"کدپستی: {address.PostalCode}",
        string.IsNullOrWhiteSpace(address.Province) ? null : $"استان: {address.Province}",
        string.IsNullOrWhiteSpace(address.City) ? null : $"شهر: {address.City}",
        address.FullAddress
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private async Task<ParsedPostalAddress> ParseRawAddressAsync(string rawAddress, CancellationToken ct)
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
                            اجزای یک نشانی پستی ایران را از کل متن استخراج کن. ترتیب خطوط هیچ معنایی ندارد و نام گیرنده ممکن است ابتدا، وسط یا انتهای متن، با یا بدون برچسب «گیرنده» یا «نام»، نوشته شده باشد. همه خطوط را بررسی کن.
                            receiverName فقط نام شخص گیرنده است. ممکن است نام و نام خانوادگی کامل، فقط نام کوچک، یا فقط یک نام خانوادگی تک‌کلمه‌ای مانند «احمدی» باشد؛ تک‌کلمه‌ای بودن را نقص تلقی نکن. @username، شناسه مشتری، نام استان، شهر، محله یا فرستنده را به‌عنوان گیرنده انتخاب نکن.
                            mobile همه شماره‌های تماس موجود در متن است: هم موبایل‌های ۱۱ رقمی که با 09 یا ۰۹ شروع می‌شوند و هم تلفن‌های ثابت، ترجیحاً همراه کد شهر. شماره‌ای که صریحاً با برچسب «تلفن»، «تلفن ثابت»، «تماس» یا «موبایل» آمده را حفظ کن. اگر بیش از یک شماره وجود دارد همه شماره‌های متمایز را به همان ترتیب متن و با «،» در یک رشته برگردان و هیچ‌کدام را حذف نکن. postalCode فقط کدپستی دقیقاً ۱۰ رقمی است؛ آن را با هیچ شماره تماسی اشتباه نگیر و مقدار بدون قرینه کدپستی را postalCode تلقی نکن.
                            city مقصد کامل پستی را نگه دارد. اگر استان در متن آمده، آن را نیز اضافه کن. اگر متن شهرستان/شهر اصلی و شهر کوچک‌تر، بخش یا مقصد محلی را هم گفته است، همه سطوح موجود را به ترتیب کلی به جزئی و با «،» برگردان؛ مثال: «فارس، داراب، دولت‌آباد». هیچ سطح صریحی را حذف نکن و این حالت را به یک نام کاهش نده.
                            fullAddress فقط ادامه نشانی قابل تحویل را نگه دارد و نام گیرنده، موبایل، کدپستی و تمام نام‌های استان/شهرستان/شهر/بخشی را که در city قرار داده‌ای از آن حذف کند تا مقصد دوباره داخل آدرس تکرار نشود. محله، خیابان، کوچه، پلاک و واحد را حفظ کن؛ مگر اینکه عیناً بخشی از city باشند.
                            هیچ مقدار مفقودی را حدس نزن و ارقام را حفظ کن. اگر کدپستی موجود نبود postalCode را رشته خالی برگردان؛ برای سایر مقدارهای پیدا نشده نیز رشته خالی برگردان تا سامانه درخواست اصلاح کند.
                            """
                    },
                    new { role = "user", content = rawAddress }
                },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "iran_postal_address",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                receiverName = new { type = "string", description = "نام شخص گیرنده، حتی اگر فقط یک نام خانوادگی تک‌کلمه‌ای باشد، مستقل از محل قرارگیری آن در متن" },
                                fullAddress = new { type = "string", description = "ادامه نشانی بدون نام گیرنده، تلفن، کدپستی و نام‌های درج‌شده در شهر مقصد" },
                                mobile = new { type = "string", description = "همه شماره‌های تماس متمایز شامل موبایل و تلفن ثابت، با ویرگول فارسی از هم جداشده" },
                                postalCode = new { type = "string", description = "فقط کدپستی دقیقاً ده‌رقمی یا رشته خالی در صورت نبودن" },
                                city = new { type = "string", description = "مقصد کامل پستی شامل همه سطوح موجود به ترتیب استان، شهرستان یا شهر اصلی، شهر یا بخش محلی؛ مانند فارس، داراب، دولت‌آباد" }
                            },
                            required = new[] { "receiverName", "fullAddress", "mobile", "postalCode", "city" },
                            additionalProperties = false
                        }
                    }
                }
            })
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");

        using var document = JsonDocument.Parse(body);
        var outputText = document.RootElement.GetProperty("output").EnumerateArray()
            .Where(value => value.TryGetProperty("content", out _))
            .SelectMany(value => value.GetProperty("content").EnumerateArray())
            .First(value => value.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .GetProperty("text").GetString();
        return JsonSerializer.Deserialize<ParsedPostalAddress>(outputText!, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("OpenAI returned an empty address result.");
    }

    private static string[] MissingFields(ParsedPostalAddress value)
    {
        var fields = new List<string>();
        if (string.IsNullOrWhiteSpace(value.ReceiverName)) fields.Add("نام گیرنده");
        if (string.IsNullOrWhiteSpace(value.FullAddress)) fields.Add("نشانی");
        if (string.IsNullOrWhiteSpace(value.Mobile)) fields.Add("تلفن");
        if (string.IsNullOrWhiteSpace(value.City)) fields.Add("شهر مقصد");
        return fields.ToArray();
    }

    private static string NormalizePostalCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var digits = new string(value.Select(ToLatinDigit).Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 10 ? value.Trim() : string.Empty;
    }

    private static char ToLatinDigit(char value) => value switch
    {
        >= '\u06F0' and <= '\u06F9' => (char)('0' + value - '\u06F0'),
        >= '\u0660' and <= '\u0669' => (char)('0' + value - '\u0660'),
        _ => value
    };

    private static IDocument BuildDocument(ParsedPostalAddress value)
    {
        var addressFontSize = value.FullAddress.Length switch
        {
            > 180 => 9.5f,
            > 125 => 10.5f,
            _ => 12.5f
        };
        return Document.Create(container => container.Page(page =>
        {
            page.Size(new PageSize(80, 50, Unit.Millimetre));
            page.Margin(3, Unit.Millimetre);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(12.5f).Bold());
            page.ContentFromRightToLeft();
            page.Content().ScaleToFit().Column(column =>
            {
                column.Spacing(1.5f);
                column.Item().Text(value.ReceiverName).Bold().FontSize(16);
                column.Item().LineHorizontal(0.6f).LineColor(Colors.Grey.Darken1);
                column.Item().Text(value.FullAddress).FontSize(addressFontSize).LineHeight(1.15f);
                column.Item().Text($"تلفن: {value.Mobile}").Bold();
                if (!string.IsNullOrWhiteSpace(value.PostalCode))
                    column.Item().Text($"کدپستی: {value.PostalCode}").Bold();
                column.Item().Text($"شهر مقصد: {value.City}").Bold().FontSize(14);
            });
        }));
    }

    public void Dispose() => _httpClient.Dispose();
}
