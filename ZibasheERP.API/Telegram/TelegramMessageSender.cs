using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ZibasheERP.API.Telegram;

public interface ITelegramMessageSender
{
    Task<TelegramSendResult> SendAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default);
}

public sealed record TelegramSendResult(bool IsSuccessful, string? Error = null);

public sealed class TelegramMessageSender : ITelegramMessageSender, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;

    public TelegramMessageSender(IOptions<TelegramOptions> options)
    {
        _botToken = options.Value.BotToken.Trim();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.telegram.org/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<TelegramSendResult> SendAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            return new TelegramSendResult(false, "Telegram bot token is not configured.");

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"bot{_botToken}/sendMessage",
                new { chat_id = chatId, text = message },
                cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(
                cancellationToken: cancellationToken);

            return response.IsSuccessStatusCode && body?.Ok == true
                ? new TelegramSendResult(true)
                : new TelegramSendResult(false, body?.Description ?? $"Telegram returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TelegramSendResult(false, exception.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record TelegramApiResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("description")] string? Description);
}
