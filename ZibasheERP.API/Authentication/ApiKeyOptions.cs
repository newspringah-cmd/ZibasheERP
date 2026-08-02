namespace ZibasheERP.API.Authentication;

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";
    public string TelegramBot { get; set; } = string.Empty;
    public string Admin { get; set; } = string.Empty;

    public bool IsValid() =>
        IsStrong(Admin) &&
        IsStrong(TelegramBot) &&
        !string.Equals(Admin, TelegramBot, StringComparison.Ordinal);

    private static bool IsStrong(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= 32;
}
