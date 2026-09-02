using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace ZibasheERP.API.Telegram;

public interface ITelegramMessageSender
{
    Task<TelegramSendResult> ConfigureAdminMenuAsync(
        string chatId,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendReplyAsync(
        string chatId,
        string message,
        long replyToMessageId,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendHtmlAsync(
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

    Task<TelegramSendResult> SendPhotoHtmlAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendStickerAsync(
        string chatId,
        string sticker,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendPhotoWithKeyboardAsync(
        string chatId,
        string photo,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendPhotoBytesWithKeyboardAsync(
        string chatId, byte[] photo, string fileName, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditPhotoCaptionAsync(
        string chatId,
        long messageId,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditPhotoAsync(
        string chatId, long messageId, string photo, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> DeleteMessageAsync(
        string chatId, long messageId, CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendDocumentAsync(
        string chatId,
        string document,
        string caption,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendDocumentWithKeyboardAsync(
        string chatId,
        byte[] document,
        string fileName,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> AnswerCallbackAsync(
        string callbackQueryId,
        string? message = null,
        CancellationToken cancellationToken = default,
        bool showAlert = false);

    Task<bool> IsChatAdministratorAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditTextAsync(
        string chatId,
        long messageId,
        string message,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditTextWithKeyboardAsync(
        string chatId, long messageId, string message,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<TelegramSendResult> EditCaptionWithKeyboardAsync(
        string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default);

    Task<bool> IsChatMemberAsync(
        string chatId,
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record TelegramSendResult(
    bool IsSuccessful,
    string? Error = null,
    long? MessageId = null,
    string? ExternalFileId = null);
public sealed record TelegramInlineButton(
    string Text,
    string? CallbackData = null,
    string? CopyText = null,
    string? Url = null);

public sealed class TelegramMessageSender : ITelegramMessageSender, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly TokenBucketRateLimiter _rateLimiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 25,
        TokensPerPeriod = 25,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 500,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

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

    public async Task<TelegramSendResult> SendReplyAsync(
        string chatId,
        string message,
        long replyToMessageId,
        CancellationToken cancellationToken = default)
        => await SendRequestAsync(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text = message,
                reply_parameters = new { message_id = replyToMessageId, allow_sending_without_reply = true }
            },
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

    public async Task<TelegramSendResult> SendHtmlAsync(
        string chatId,
        string message,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendMessage",
            new { chat_id = chatId, text = message, parse_mode = "HTML" },
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
        if (!string.IsNullOrWhiteSpace(button.Url))
            value["url"] = button.Url;
        return value;
    }

    public async Task<TelegramSendResult> ConfigureAdminMenuAsync(
        string chatId,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(chatId, out var numericChatId) || numericChatId == 0)
            return new TelegramSendResult(false, "Telegram admin chat ID is invalid.");

        return await SendRequestAsync(
            "setMyCommands",
            new
            {
                commands = new[]
                {
                    new { command = "admin", description = "⚙️ پنل مدیریت" }
                },
                scope = new { type = "chat", chat_id = numericChatId }
            },
            cancellationToken);
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

    public async Task<TelegramSendResult> SendPhotoHtmlAsync(
        string chatId,
        string photo,
        string caption,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendPhoto",
            new { chat_id = chatId, photo, caption, parse_mode = "HTML" },
            cancellationToken);

    public async Task<TelegramSendResult> SendStickerAsync(
        string chatId,
        string sticker,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendSticker",
            new { chat_id = chatId, sticker },
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
                parse_mode = "HTML",
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> SendPhotoBytesWithKeyboardAsync(
        string chatId, byte[] photo, string fileName, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId, Encoding.UTF8), "chat_id");
        content.Add(new StringContent(caption, Encoding.UTF8), "caption");
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
        }), Encoding.UTF8, "application/json"), "reply_markup");
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "photo", fileName);
        return await SendMultipartRequestAsync("sendPhoto", content, cancellationToken);
    }

    private async Task<TelegramSendResult> SendMultipartRequestAsync(
        string method, MultipartFormDataContent content, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync($"./bot{_botToken}/{method}", content, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode || body?.Ok != true)
            return new TelegramSendResult(false, body?.Description ?? $"Telegram HTTP {(int)response.StatusCode}");
        return new TelegramSendResult(true, MessageId: ReadMessageId(body.Result),
            ExternalFileId: ReadPhotoFileId(body.Result));
    }

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
                parse_mode = "HTML",
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            },
            cancellationToken);

    public async Task<TelegramSendResult> EditPhotoAsync(
        string chatId, long messageId, string photo, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "editMessageMedia",
            new
            {
                chat_id = chatId,
                message_id = messageId,
                media = new { type = "photo", media = photo, caption, parse_mode = "HTML" },
                reply_markup = new
                {
                    inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
                }
            }, cancellationToken);

    public async Task<TelegramSendResult> DeleteMessageAsync(
        string chatId, long messageId, CancellationToken cancellationToken = default) =>
        await SendRequestAsync("deleteMessage", new { chat_id = chatId, message_id = messageId }, cancellationToken);

    public async Task<TelegramSendResult> EditTextAsync(
        string chatId,
        long messageId,
        string message,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "editMessageText",
            new { chat_id = chatId, message_id = messageId, text = message },
            cancellationToken);

    public async Task<TelegramSendResult> EditTextWithKeyboardAsync(
        string chatId, long messageId, string message,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync("editMessageText", new
        {
            chat_id = chatId, message_id = messageId, text = message,
            reply_markup = new
            {
                inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
            }
        }, cancellationToken);

    public async Task<TelegramSendResult> EditCaptionWithKeyboardAsync(
        string chatId, long messageId, string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync("editMessageCaption", new
        {
            chat_id = chatId, message_id = messageId, caption,
            reply_markup = new
            {
                inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
            }
        }, cancellationToken);

    public async Task<TelegramSendResult> SendDocumentAsync(
        string chatId,
        string document,
        string caption,
        CancellationToken cancellationToken = default) =>
        await SendRequestAsync(
            "sendDocument",
            new { chat_id = chatId, document, caption },
            cancellationToken);

    public async Task<TelegramSendResult> SendDocumentWithKeyboardAsync(
        string chatId,
        byte[] document,
        string fileName,
        string caption,
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_botToken))
            return new TelegramSendResult(false, "Telegram bot token is not configured.");

        using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
            return new TelegramSendResult(false, "Telegram send queue is full.");

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(chatId, Encoding.UTF8), "chat_id");
            content.Add(new StringContent(caption, Encoding.UTF8), "caption");
            content.Add(new StringContent(JsonSerializer.Serialize(new
            {
                inline_keyboard = rows.Select(row => row.Select(BuildInlineButton).ToArray()).ToArray()
            }), Encoding.UTF8, "application/json"), "reply_markup");

            var fileContent = new ByteArrayContent(document);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(fileContent, "document", fileName);

            using var response = await _httpClient.PostAsync(
                $"./bot{_botToken}/sendDocument", content, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(
                cancellationToken: cancellationToken);
            if (response.IsSuccessStatusCode && body?.Ok == true)
            {
                return new TelegramSendResult(
                    true,
                    MessageId: ReadMessageId(body.Result),
                    ExternalFileId: ReadDocumentFileId(body.Result));
            }

            return new TelegramSendResult(false,
                body?.Description ?? $"Telegram returned HTTP {(int)response.StatusCode}.");
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

    public async Task<TelegramSendResult> AnswerCallbackAsync(
        string callbackQueryId,
        string? message = null,
        CancellationToken cancellationToken = default,
        bool showAlert = false) =>
        await SendRequestAsync(
            "answerCallbackQuery",
            new { callback_query_id = callbackQueryId, text = message, show_alert = showAlert },
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
                return new TelegramSendResult(false, "Telegram send queue is full.");
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"./bot{_botToken}/{method}", request, cancellationToken);
                var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(
                    cancellationToken: cancellationToken);
                if (response.IsSuccessStatusCode && body?.Ok == true)
                    return new TelegramSendResult(true, MessageId: ReadMessageId(body.Result));
                var retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                if (!retryable || attempt == 2)
                    return new TelegramSendResult(false,
                        body?.Description ?? $"Telegram returned HTTP {(int)response.StatusCode}.");
                var delay = TimeSpan.FromSeconds(Math.Clamp(body?.Parameters?.RetryAfter ?? attempt + 1, 1, 10));
                await Task.Delay(delay, cancellationToken);
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
        return new TelegramSendResult(false, "Telegram request failed after retries.");
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _httpClient.Dispose();
    }

    private sealed record TelegramApiResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("result")] JsonElement? Result,
        [property: JsonPropertyName("parameters")] TelegramApiParameters? Parameters);

    private sealed record TelegramApiParameters(
        [property: JsonPropertyName("retry_after")] int? RetryAfter);

    private static long? ReadMessageId(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("message_id", out var messageId) &&
        messageId.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? ReadDocumentFileId(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("document", out var document) &&
        document.TryGetProperty("file_id", out var fileId)
            ? fileId.GetString()
            : null;

    private static string? ReadPhotoFileId(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("photo", out var photo) &&
        photo.ValueKind == JsonValueKind.Array && photo.GetArrayLength() > 0
            ? photo.EnumerateArray().Last().TryGetProperty("file_id", out var fileId)
                ? fileId.GetString() : null
            : null;

    private sealed record TelegramChatMemberApiResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] TelegramChatMemberApiResult? Result);

    private sealed record TelegramChatMemberApiResult(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("is_member")] bool? IsMember);
}
