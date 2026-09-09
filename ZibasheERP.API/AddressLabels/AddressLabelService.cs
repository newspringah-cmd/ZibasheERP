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
            var parsed = string.Equals(address.Description, "آدرس خام ثبت‌شده توسط حسابدار", StringComparison.Ordinal)
                ? await ParseRawAddressAsync(address.FullAddress, cancellationToken)
                : new ParsedPostalAddress(
                    address.ReceiverName.Trim(), address.FullAddress.Trim(), address.Mobile.Trim(),
                    address.PostalCode.Trim(), address.City.Trim());

            var missing = MissingFields(parsed);
            if (missing.Length > 0)
                return new AddressLabelResult(false, Error:
                    $"اطلاعات {string.Join("، ", missing)} در متن آدرس پیدا نشد؛ آدرس را کامل‌تر ثبت کنید.");

            return new AddressLabelResult(true, BuildPdf(parsed));
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

    private async Task<ParsedPostalAddress> ParseRawAddressAsync(string rawAddress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(new
            {
                model = _options.OpenAiModel,
                input = new object[]
                {
                    new { role = "system", content = "متن نشانی پستی ایران را فقط استخراج کن. هیچ مقدار مفقودی را حدس نزن. ارقام را حفظ کن و نشانی کامل را بدون نام، تلفن و کدپستی برگردان." },
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
                                receiverName = new { type = "string" },
                                fullAddress = new { type = "string" },
                                mobile = new { type = "string" },
                                postalCode = new { type = "string" },
                                city = new { type = "string" }
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
        if (string.IsNullOrWhiteSpace(value.PostalCode)) fields.Add("کدپستی");
        if (string.IsNullOrWhiteSpace(value.City)) fields.Add("شهر مقصد");
        return fields.ToArray();
    }

    private static byte[] BuildPdf(ParsedPostalAddress value)
    {
        var addressFontSize = value.FullAddress.Length switch
        {
            > 180 => 8.5f,
            > 125 => 9.5f,
            _ => 10.5f
        };
        return Document.Create(container => container.Page(page =>
        {
            page.Size(new PageSize(80, 50, Unit.Millimetre));
            page.Margin(3, Unit.Millimetre);
            page.DefaultTextStyle(style => style.FontFamily(FontFamily).FontSize(10.5f).Bold());
            page.ContentFromRightToLeft();
            page.Content().ScaleToFit().Column(column =>
            {
                column.Spacing(1.5f);
                column.Item().Text(value.ReceiverName).Bold().FontSize(14);
                column.Item().LineHorizontal(0.6f).LineColor(Colors.Grey.Darken1);
                column.Item().Text(value.FullAddress).FontSize(addressFontSize).LineHeight(1.15f);
                column.Item().Text($"تلفن: {value.Mobile}").Bold();
                column.Item().Text($"کدپستی: {value.PostalCode}").Bold();
                column.Item().Text($"شهر مقصد: {value.City}").Bold().FontSize(12);
            });
        })).GeneratePdf();
    }

    public void Dispose() => _httpClient.Dispose();
}
