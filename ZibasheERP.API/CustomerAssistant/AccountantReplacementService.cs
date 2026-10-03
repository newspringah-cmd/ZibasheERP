using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZibasheERP.API.AddressLabels;

namespace ZibasheERP.API.CustomerAssistant;

public sealed record AccountantAnswerExample(string Question, string Answer);
public sealed record AccountantReplacementResult(bool ShouldAnswer, string? Answer = null, string? Error = null);

public interface IAccountantReplacementService
{
    bool IsEnabled { get; }
    Task<AccountantReplacementResult> AnswerAsync(
        string question,
        IReadOnlyCollection<AccountantAnswerExample> examples,
        CancellationToken cancellationToken);
}

public sealed class AccountantReplacementService : IAccountantReplacementService, IDisposable
{
    private readonly AddressLabelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountantReplacementService> _logger;

    public AccountantReplacementService(
        IOptions<AddressLabelOptions> options,
        ILogger<AccountantReplacementService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(25)
        };
        if (!string.IsNullOrWhiteSpace(_options.OpenAiApiKey))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);
    }

    public bool IsEnabled =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.OpenAiApiKey) &&
        !string.IsNullOrWhiteSpace(_options.OpenAiModel);

    public async Task<AccountantReplacementResult> AnswerAsync(
        string question,
        IReadOnlyCollection<AccountantAnswerExample> examples,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || examples.Count < 3)
            return new(false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses")
            {
                Content = JsonContent.Create(new
                {
                    model = _options.OpenAiModel,
                    store = false,
                    reasoning = new { effort = "low" },
                    max_output_tokens = 1200,
                    input = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = """
                                تو «زیبا»، دستیار موقت حسابدار زیباشی در گروه مشتری هستی.
                                فقط وقتی پاسخ بده که نمونه‌های واقعی حسابدار ارائه‌شده، پاسخ همان موضوع را روشن و بدون ابهام مشخص کنند.
                                لحن، کوتاهی و شیوه بیان حسابدار را رعایت کن، اما متن نمونه‌ها را کورکورانه کپی نکن.
                                مبلغ، زمان، وضعیت، موجودی، تعهد یا واقعیتی را که در سؤال و نمونه‌های مرتبط نیست حدس نزن.
                                درباره پرداخت، فاکتور، تسویه، بدهی، واریز، شماره کارت و هر موضوع مالی پاسخ نده.
                                درباره وضعیت عطر یا پیشنهاد و انتخاب عطر پاسخ نده؛ این موارد مسیر جداگانه دارند.
                                اگر سؤال مبهم، شخصی، حساس، جدید یا خارج از نمونه‌هاست shouldAnswer=false برگردان.
                                فقط در اطمینان بالا shouldAnswer=true برگردان. پاسخ فارسی، کوتاه و محترمانه باشد.
                                امضای ربات را در answer ننویس؛ سامانه جداگانه اضافه می‌کند.
                                """
                        },
                        new
                        {
                            role = "user",
                            content = $"پرسش جدید مشتری:\n{question}\n\nنمونه‌های واقعی پرسش و پاسخ حسابدار (JSON):\n{JsonSerializer.Serialize(examples)}"
                        }
                    },
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "accountant_replacement_answer",
                            strict = true,
                            schema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    shouldAnswer = new { type = "boolean" },
                                    answer = new { type = "string" }
                                },
                                required = new[] { "shouldAnswer", "answer" },
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
                    value.TryGetProperty("type", out var type) && type.GetString() == "output_text");
            if (outputText.ValueKind == JsonValueKind.Undefined ||
                !outputText.TryGetProperty("text", out var textValue))
                throw new InvalidOperationException("OpenAI returned no accountant replacement text.");

            var payload = JsonSerializer.Deserialize<ResponsePayload>(
                textValue.GetString() ?? string.Empty,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null || !payload.ShouldAnswer || string.IsNullOrWhiteSpace(payload.Answer))
                return new(false);
            return new(true, payload.Answer.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Accountant replacement answer generation failed.");
            return new(false, Error: "generation_failed");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record ResponsePayload(bool ShouldAnswer, string Answer);
}
