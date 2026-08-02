namespace ZibasheERP.API.Authentication;

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";
    public string TelegramBot { get; set; } = string.Empty;
    public string Admin { get; set; } = string.Empty;
    public string N8n { get; set; } = string.Empty;

    public bool IsValid(bool requireN8n = false) =>
        IsStrong(Admin) &&
        IsStrong(TelegramBot) &&
        !string.Equals(Admin, TelegramBot, StringComparison.Ordinal) &&
        (!requireN8n ||
            (IsStrong(N8n) &&
             !string.Equals(N8n, Admin, StringComparison.Ordinal) &&
             !string.Equals(N8n, TelegramBot, StringComparison.Ordinal)));

    private static bool IsStrong(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= 32;
}
