namespace ZibasheERP.API.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string AdminChatId { get; set; } = string.Empty;
    public string SalesChannelId { get; set; } = string.Empty;
    public string SalesDiscussionChatId { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;
}
