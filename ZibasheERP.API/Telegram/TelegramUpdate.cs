using System.Text.Json.Serialization;

namespace ZibasheERP.API.Telegram;

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("callback_query")] TelegramCallbackQuery? CallbackQuery,
    [property: JsonPropertyName("my_chat_member")] TelegramChatMemberUpdated? MyChatMember = null);

public sealed record TelegramMessage(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("chat")] TelegramChat Chat,
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("contact")] TelegramContact? Contact);

public sealed record TelegramChat(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("username")] string? Username = null);

public sealed record TelegramUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string? Username);

public sealed record TelegramContact(
    [property: JsonPropertyName("phone_number")] string PhoneNumber,
    [property: JsonPropertyName("user_id")] long? UserId);

public sealed record TelegramCallbackQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] TelegramUser From,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("data")] string? Data);

public sealed record TelegramChatMemberUpdated(
    [property: JsonPropertyName("chat")] TelegramChat Chat,
    [property: JsonPropertyName("new_chat_member")] TelegramChatMember NewChatMember);

public sealed record TelegramChatMember(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("is_member")] bool? IsMember,
    [property: JsonPropertyName("can_send_messages")] bool? CanSendMessages);
