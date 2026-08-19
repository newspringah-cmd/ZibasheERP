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

    Task<TelegramSendResult> SendForceReplyAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendPhotoAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendPhotoWithKeyboardAsync(
        string chatId,
        string photo,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditPhotoCaptionAsync(
        string chatId,
        long messageId,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
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

    Task<bool> IsChatAdministratorAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<bool> IsChatMemberAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record TelegramSendResult(
    bool IsSuccessful,
    string? Error = null,
    long? MessageId = null);
public sealed record TelegramInlineButton(
    string Text,
    string? CallbackData = null,
    string? CopyText = null);

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
                        row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> SendForceReplyAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text = message,
                reply_markup = new { force_reply = true, selective = true }
            },
            cancellationToken);

    private static Dictionary<string, object> BuildInlineButton(TelegramInlineButton button)
    {
        var value = new Dictionary<string, object> { ["text"] = button.Text };
        if (!string.IsNullOrWhiteSpace(button.CallbackData))
            value["callback_data"] = button.CallbackData;
        if (!string.IsNullOrWhiteSpace(button.CopyText))
            value["copy_text"] = new { text = button.CopyText };
        return value;
    }

    public async Task<TelegramSendResult> SendPhotoAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendPhoto",
            new { chat_id = chatId, photo, caption },
            cancellationToken);

    public async Task<TelegramSendResult> SendPhotoWithKeyboardAsync(
        string chatId,
        string photo,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendPhoto",
            new
            {
                chat_id = chatId,
                photo,
                caption,
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> EditPhotoCaptionAsync(
        string chatId,
        long messageId,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "editMessageCaption",
            new
            {
                chat_id = chatId,
                message_id = messageId,
                caption,
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            },
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

    public async Task<bool> IsChatAdministratorAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            return false;

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"./bot{_botToken}/getChatMember",
                new { chat_id = chatId, user_id = userId },
                cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TelegramChatMemberApiResponse>(
                cancellationToken: cancellationToken);
            return response.IsSuccessStatusCode &&
                body?.Ok == true &&
                body.Result?.Status is "creator" or "administrator";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public async Task<bool> IsChatMemberAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            return false;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"./bot{_botToken}/getChatMember",
                new { chat_id = chatId, user_id = userId },
                cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TelegramChatMemberApiResponse>(
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode || body?.Ok != true || body.Result is null)
                return false;
            return body.Result.Status is "creator" or "administrator" or "member" ||
                body.Result.Status == "restricted" && body.Result.IsMember == true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

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
                ? new TelegramSendResult(true, MessageId: ReadMessageId(body.Result))
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
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("result")] JsonElement? Result);

    private static long? ReadMessageId(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("message_id", out var messageId) &&
        messageId.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private sealed record TelegramChatMemberApiResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] TelegramChatMemberApiResult? Result);

    private sealed record TelegramChatMemberApiResult(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("is_member")] bool? IsMember);
}
