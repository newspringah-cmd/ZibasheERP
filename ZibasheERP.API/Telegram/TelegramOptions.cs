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
    public string SalesAuditChatId { get; set; } = string.Empty;
    public string LowStockAlertChatId { get; set; } = string.Empty;
    public string PromotionAlertChatId { get; set; } = string.Empty;
    public string CompletedSalesListsChatId { get; set; } = string.Empty;
    public string InvoiceFailureChatId { get; set; } = string.Empty;
    public string DecantChatId { get; set; } = string.Empty;
    public string LabelPrintChatId { get; set; } = string.Empty;
    public string NewPaymentsChatId { get; set; } = string.Empty;
    public string InventoryChatId { get; set; } = string.Empty;
    public string InvoiceGreetingStickerFileId { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;
    public int MessageDelayMilliseconds { get; set; } = 1000;
    public int RecipientDelayMilliseconds { get; set; } = 2000;
}
