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

    Task<TelegramSendResult> RequestContactAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendInlineKeyboardAsync(
        string chatId,
        string message,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendPhotoAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendDocumentAsync(
        string chatId,
        string document,
        string caption,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> AnswerCallbackAsync(
        string callbackQueryId,
        string? message = null,
        CancellationToken cancellationToken = default);
}

public sealed record TelegramSendResult(bool IsSuccessful, string? Error = null);
public sealed record TelegramInlineButton(string Text, string CallbackData);

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
        => await SendRequestAsync(
            "sendMessage",
            new { chat_id = chatId, text = message },
            cancellationToken);

    public async Task<TelegramSendResult> RequestContactAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text = message,
                reply_markup = new
                {
                    keyboard = new[]
                    {
                        new[] { new { text = "ارسال شماره موبایل", request_contact = true } }
                    },
                    resize_keyboard = true,
                    one_time_keyboard = true
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> SendInlineKeyboardAsync(
        string chatId,
        string message,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text = message,
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row =>
                        row.Select(button => new
                        {
                            text = button.Text,
                            callback_data = button.CallbackData
                        }).ToArray()).ToArray()
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> SendPhotoAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendPhoto",
            new { chat_id = chatId, photo, caption },
            cancellationToken);

    public async Task<TelegramSendResult> SendDocumentAsync(
        string chatId,
        string document,
        string caption,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendDocument",
            new { chat_id = chatId, document, caption },
            cancellationToken);

    public async Task<TelegramSendResult> AnswerCallbackAsync(
        string callbackQueryId,
        string? message = null,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "answerCallbackQuery",
            new { callback_query_id = callbackQueryId, text = message },
            cancellationToken);

    private async Task<TelegramSendResult> SendRequestAsync(
        string method,
        object request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            return new TelegramSendResult(false, "Telegram bot token is not configured.");

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"./bot{_botToken}/{method}",
                request,
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
