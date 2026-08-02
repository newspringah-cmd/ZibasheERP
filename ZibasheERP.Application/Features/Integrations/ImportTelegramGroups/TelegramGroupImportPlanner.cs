namespace ZibasheERP.Application.Features.Integrations.ImportTelegramGroups;

public sealed record TelegramGroupImportRow(
    int RowNumber,
    string ChatId,
    string Title,
    string? GroupUsername,
    string CustomerUsername,
    string? GroupType);

public sealed record TelegramGroupImportIssue(
    int? RowNumber,
    string Code,
    string Message,
    string? CustomerUsername = null,
    string? ChatId = null);

public sealed record TelegramGroupImportPlan(
    IReadOnlyCollection<TelegramGroupImportRow> Selected,
    IReadOnlyCollection<TelegramGroupImportIssue> Issues);

public static class TelegramGroupImportPlanner
{
    public static TelegramGroupImportPlan Create(IEnumerable<TelegramGroupImportRow> source)
    {
        var selected = new List<TelegramGroupImportRow>();
        var issues = new List<TelegramGroupImportIssue>();
        var valid = new List<TelegramGroupImportRow>();
        var seenChatIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in source)
        {
            var chatId = row.ChatId.Trim();
            var title = row.Title.Trim().TrimStart('\'');
            var customerUsername = NormalizeUsername(row.CustomerUsername);
            if (!long.TryParse(chatId, out var numericChatId) || numericChatId >= 0)
            {
                issues.Add(new(row.RowNumber, "invalid_chat_id", "شناسه گروه معتبر نیست.", customerUsername, chatId));
                continue;
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                issues.Add(new(row.RowNumber, "missing_title", "عنوان گروه خالی است.", customerUsername, chatId));
                continue;
            }
            if (string.IsNullOrWhiteSpace(customerUsername))
            {
                issues.Add(new(row.RowNumber, "missing_customer_username", "username مشتری موجود نیست.", null, chatId));
                continue;
            }
            if (!seenChatIds.Add(chatId))
            {
                issues.Add(new(row.RowNumber, "duplicate_chat_id", "شناسه گروه در فایل تکراری است.", customerUsername, chatId));
                continue;
            }

            valid.Add(row with
            {
                ChatId = chatId,
                Title = title,
                GroupUsername = NormalizeUsername(row.GroupUsername),
                CustomerUsername = customerUsername
            });
        }

        foreach (var customerRows in valid.GroupBy(
                     row => row.CustomerUsername,
                     StringComparer.OrdinalIgnoreCase))
        {
            var rows = customerRows.ToArray();
            if (rows.Length == 1)
            {
                selected.Add(rows[0]);
                continue;
            }

            var supergroups = rows.Where(IsSupergroup).ToArray();
            if (supergroups.Length == 1)
            {
                selected.Add(supergroups[0]);
                continue;
            }

            issues.Add(new(
                null,
                "ambiguous_customer_groups",
                $"برای مشتری {rows.Length} گروه وجود دارد و مقصد اصلی قابل تشخیص نیست.",
                customerRows.Key));
        }

        return new(selected, issues);
    }

    public static string NormalizeUsername(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().TrimStart('@').ToLowerInvariant();

    private static bool IsSupergroup(TelegramGroupImportRow row) =>
        string.Equals(row.GroupType, "supergroup", StringComparison.OrdinalIgnoreCase) ||
        row.ChatId.StartsWith("-100", StringComparison.Ordinal);
}
