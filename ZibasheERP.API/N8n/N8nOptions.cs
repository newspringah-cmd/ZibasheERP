namespace ZibasheERP.API.N8n;

public sealed class N8nOptions
{
    public const string SectionName = "N8n";

    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 8;
}
