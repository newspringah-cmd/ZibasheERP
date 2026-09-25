using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZibasheERP.API.AddressLabels;

namespace ZibasheERP.API.CustomerAssistant;

public sealed record PerfumeRecommendationCatalogItem(
    int ListCode,
    string PersianName,
    string EnglishName,
    string Brand,
    string Gender,
    string TopNotes,
    string MiddleNotes,
    string BaseNotes,
    string Accords,
    int RemainingVolumeMl);

public sealed record PerfumeRecommendationResult(
    bool IsSuccessful,
    string? Answer = null,
    IReadOnlyCollection<int>? ListCodes = null,
    string? Error = null);

public interface IPerfumeRecommendationService
{
    bool IsEnabled { get; }

    Task<PerfumeRecommendationResult> RecommendAsync(
        string question,
        IReadOnlyCollection<PerfumeRecommendationCatalogItem> catalog,
        CancellationToken cancellationToken);
}

public sealed class PerfumeRecommendationService : IPerfumeRecommendationService, IDisposable
{
    private readonly AddressLabelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PerfumeRecommendationService> _logger;

    public PerfumeRecommendationService(
        IOptions<AddressLabelOptions> options,
        ILogger<PerfumeRecommendationService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(25)
        };
        if (!string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);
        }
    }

    public bool IsEnabled =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.OpenAiApiKey) &&
        !string.IsNullOrWhiteSpace(_options.OpenAiModel);

    public async Task<PerfumeRecommendationResult> RecommendAsync(
        string question,
        IReadOnlyCollection<PerfumeRecommendationCatalogItem> catalog,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return new(false, Error: "راهنمای هوشمند عطر در حال حاضر فعال نیست.");
        if (catalog.Count == 0)
            return new(false, Error: "در حال حاضر لیست باز مناسبی برای پیشنهاد وجود ندارد.");

        try
        {
            var catalogJson = JsonSerializer.Serialize(catalog);
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
            {
                Content = JsonContent.Create(new
                {
                    model = _options.OpenAiModel,
                    store = false,
                    reasoning = new { effort = "low" },
                    max_output_tokens = 2400,
                    input = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = """
                                تو «زیبا»، دستیار انتخاب عطر فروشگاه زیباشی هستی. پاسخ را کوتاه، دوستانه و فارسی بنویس.
                                فقط از فهرست لیست‌های باز ارائه‌شده پیشنهاد بده و هیچ عطر، نت، ویژگی، موجودی یا کد لیستی را حدس نزن.
                                حداکثر سه پیشنهاد بده. برای هر پیشنهاد نام عطر، کد لیست و دلیل کوتاه مرتبط با خواسته مشتری را ذکر کن.
                                listCodes باید فقط کد دقیق همان لیست‌هایی باشد که در answer پیشنهاد داده‌ای. اگر فقط سؤال تکمیلی می‌پرسی یا پیشنهادی نداری، آرایه خالی برگردان.
                                اگر داده‌های نت یا آکورد برای نتیجه قطعی کافی نیست، صریح بگو اطلاعات ثبت‌شده کافی نیست.
                                اگر سؤال برای پیشنهاد دقیق به اطلاعات بیشتری مثل رایحه دلخواه، فصل، جنسیت یا موقعیت مصرف نیاز دارد، فقط یک سؤال روشن بپرس.
                                درباره پرداخت، فاکتور، تسویه، بدهی، اعتبار یا شماره کارت پاسخ نده و کاربر را به حسابداری زیباشی ارجاع بده.
                                درباره درمان، حساسیت یا ایمنی پزشکی ادعای قطعی نکن.
                                در متن answer امضای ربات یا عنوان «زیبا» را اضافه نکن؛ سامانه آن را جداگانه درج می‌کند.
                                """
                        },
                        new
                        {
                            role = "user",
                            content = $"پرسش مشتری:\n{question}\n\nلیست‌های باز زیباشی (JSON):\n{catalogJson}"
                        }
                    },
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "perfume_recommendation",
                            strict = true,
                            schema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    answer = new
                                    {
                                        type = "string",
                                        description = "پاسخ کوتاه فارسی و محدود به اطلاعات فهرست ارائه‌شده"
                                    },
                                    listCodes = new
                                    {
                                        type = "array",
                                        description = "حداکثر سه کد لیست باز که در پاسخ پیشنهاد شده‌اند",
                                        items = new { type = "integer" }
                                    }
                                },
                                required = new[] { "answer", "listCodes" },
                                additionalProperties = false
                            }
                        }
                    }
                })
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"OpenAI returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");

            using var document = JsonDocument.Parse(body);
            var outputText = document.RootElement.GetProperty("output").EnumerateArray()
                .Where(value => value.TryGetProperty("content", out _))
                .SelectMany(value => value.GetProperty("content").EnumerateArray())
                .FirstOrDefault(value =>
                    value.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text");
            if (outputText.ValueKind == JsonValueKind.Undefined ||
                !outputText.TryGetProperty("text", out var textValue))
            {
                var root = document.RootElement;
                var status = root.TryGetProperty("status", out var statusValue)
                    ? statusValue.GetString() ?? "unknown"
                    : "unknown";
                var incompleteReason = root.TryGetProperty("incomplete_details", out var incomplete) &&
                                       incomplete.ValueKind == JsonValueKind.Object &&
                                       incomplete.TryGetProperty("reason", out var reason)
                    ? reason.GetString() ?? "none"
                    : "none";
                var contentTypes = root.GetProperty("output").EnumerateArray()
                    .Where(value => value.TryGetProperty("content", out _))
                    .SelectMany(value => value.GetProperty("content").EnumerateArray())
                    .Select(value => value.TryGetProperty("type", out var type)
                        ? type.GetString() ?? "unknown"
                        : "unknown")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                throw new InvalidOperationException(
                    $"OpenAI returned no perfume recommendation text. " +
                    $"Status={status}; IncompleteReason={incompleteReason}; " +
                    $"ContentTypes={string.Join(',', contentTypes)}.");
            }

            var parsed = JsonSerializer.Deserialize<RecommendationPayload>(
                textValue.GetString() ?? string.Empty,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(parsed?.Answer))
                throw new InvalidOperationException("OpenAI returned an empty perfume recommendation.");

            var validCodes = (parsed.ListCodes ?? [])
                .Where(code => catalog.Any(item => item.ListCode == code))
                .Distinct()
                .Take(3)
                .ToArray();
            return new(true, parsed.Answer.Trim(), validCodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Customer perfume recommendation generation failed.");
            return new(false, Error: "الان نتونستم پیشنهاد مطمئنی آماده کنم؛ لطفاً کمی بعد دوباره بپرسید.");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record RecommendationPayload(string Answer, int[]? ListCodes);
}
