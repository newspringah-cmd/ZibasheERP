using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.CustomerAssistant;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string LearningAccountantUsername = "accountantzibashe";
    private const string LearningManagerUsername = "zahraa_frj";
    private static readonly Regex SensitiveLearningNumberPattern = new(
        @"(?:[0-9۰-۹٠-٩][\s\-]*){8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private async Task CaptureAccountantLearningMessageAsync(
        TelegramMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            if (message.From is null || message.From.IsBot)
                return;

            var text = FirstTelegramMessageText(message);
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/') ||
                ContainsSensitiveAccountantLearningData(text))
                return;

            var setting = await _db.TelegramAccountantLearningSettings
                .AsNoTracking()
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (setting is not null && !setting.IsLearningEnabled)
                return;

            var chatId = message.Chat.Id.ToString(CultureInfo.InvariantCulture);
            var isConnectedCustomerGroup = await _db.CustomerTelegramGroups
                .AsNoTracking()
                .AnyAsync(value =>
                    !value.IsDeleted && value.IsActive && value.ChatId == chatId,
                    cancellationToken);
            if (!isConnectedCustomerGroup)
                return;

            var alreadyCaptured = await _db.TelegramAccountantLearningMessages
                .AsNoTracking()
                .AnyAsync(value => value.ChatId == chatId && value.MessageId == message.MessageId,
                    cancellationToken);
            if (alreadyCaptured)
                return;

            var normalizedUsername = NormalizeLearningUsername(message.From.Username);
            var sample = new TelegramAccountantLearningMessage
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                ChatId = chatId,
                ChatTitle = TruncateLearningValue(message.Chat.Title, 250),
                MessageId = message.MessageId,
                ReplyToMessageId = message.ReplyToMessage?.MessageId,
                SenderTelegramUserId = message.From.Id.ToString(CultureInfo.InvariantCulture),
                SenderUsername = TruncateLearningValue(message.From.Username?.Trim().TrimStart('@'), 64),
                SenderDisplayName = TruncateLearningValue(
                    string.Join(' ', new[] { message.From.FirstName, message.From.LastName }
                        .Where(value => !string.IsNullOrWhiteSpace(value))),
                    200),
                IsAccountant = normalizedUsername == LearningAccountantUsername,
                MessageText = text.Trim()
            };
            _db.TelegramAccountantLearningMessages.Add(sample);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Learning is observational only and must never interrupt commands or customer replies.
            _logger.LogWarning(
                exception,
                "Capturing accountant learning message failed for Telegram chat {TelegramGroupChatId} and message {TelegramMessageId}.",
                message.Chat.Id,
                message.MessageId);
        }
    }

    private async Task<bool> TryHandleAccountantReplacementQuestionAsync(
        TelegramMessage message, CancellationToken cancellationToken)
    {
        if (!CanQueueAccountantQuestion(message) || !await IsAccountantReplacementReplyEnabledAsync(cancellationToken)) return false;
        await QueueAccountantQuestionAsync(message,DateTime.UtcNow,cancellationToken);
        return true;
    }

    private async Task<string?> GenerateAccountantReplacementAnswerAsync(TelegramMessage message,CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-31);
        var history = await _db.TelegramAccountantLearningMessages.AsNoTracking()
            .Where(value => !value.IsDeleted && value.CreatedAt >= since)
            .OrderByDescending(value => value.CreatedAt)
            .Take(1500)
            .Select(value => new
            {
                value.ChatId,
                value.MessageId,
                value.ReplyToMessageId,
                value.IsAccountant,
                value.MessageText,
                value.CreatedAt
            })
            .ToArrayAsync(cancellationToken);

        history=history.Where(value=>!ContainsSensitiveAccountantLearningData(value.MessageText)).ToArray();
        var examples = new List<AccountantAnswerExample>();
        foreach (var answer in history.Where(value => value.IsAccountant))
        {
            var question = answer.ReplyToMessageId.HasValue
                ? history.FirstOrDefault(value =>
                    value.ChatId == answer.ChatId &&
                    value.MessageId == answer.ReplyToMessageId.Value &&
                    !value.IsAccountant)
                : history
                    .Where(value =>
                        value.ChatId == answer.ChatId &&
                        !value.IsAccountant &&
                        value.MessageId < answer.MessageId &&
                        answer.CreatedAt - value.CreatedAt <= TimeSpan.FromHours(12))
                    .OrderByDescending(value => value.MessageId)
                    .FirstOrDefault();
            if (question is not null)
                examples.Add(new AccountantAnswerExample(question.MessageText, answer.MessageText));
        }

        var questionTokens = LearningTokens(message.Text!);
        var relevant = examples
            .Select(example => new
            {
                Example = example,
                Score = LearningTokens(example.Question).Count(questionTokens.Contains)
            })
            .Where(value => value.Score > 0)
            .OrderByDescending(value => value.Score)
            .Take(12)
            .Select(value => value.Example)
            .ToArray();
        if (relevant.Length < 3)
            return null;

        var result = await _accountantReplacementService.AnswerAsync(
            message.Text!.Trim(), relevant, cancellationToken);
        if(result.Error is not null)throw new InvalidOperationException("AccountantGenerationFailed");
        if (!result.ShouldAnswer || string.IsNullOrWhiteSpace(result.Answer) ||
            ContainsSensitiveAccountantLearningData(result.Answer))
            return null;

        return ZibaAssistantIdentity.Signature + "\n\n" + result.Answer;
    }

    private async Task ToggleAccountantLearningAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (!IsAccountantLearningManager(callback.From.Username))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این تنظیم فقط برای مدیر تعیین‌شده قابل تغییر است.",
                cancellationToken: cancellationToken);
            return;
        }

        var setting = await _db.TelegramAccountantLearningSettings
            .Where(value=>!value.IsDeleted)
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (setting is null)
        {
            setting = new TelegramAccountantLearningSetting
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                IsLearningEnabled = false,
                IsAutoReplyEnabled = false,
                UpdatedByTelegramUserId = callback.From.Id.ToString(CultureInfo.InvariantCulture)
            };
            _db.TelegramAccountantLearningSettings.Add(setting);
        }
        else
        {
            setting.IsLearningEnabled = !setting.IsLearningEnabled;
            setting.UpdatedAt = now;
            setting.UpdatedByTelegramUserId = callback.From.Id.ToString(CultureInfo.InvariantCulture);
        }

        if (setting.IsLearningEnabled && setting.CollectionStartedAt is null)
            setting.CollectionStartedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        await _sender.AnswerCallbackAsync(
            callback.Id,
            setting.IsLearningEnabled ? "یادگیری پاسخ حسابدار فعال شد." : "یادگیری پاسخ حسابدار غیرفعال شد.",
            cancellationToken: cancellationToken);
        await SendAccountantAssistantSettingsAsync(callback.Message!.Chat.Id, callback.From, cancellationToken);
    }

    private async Task ToggleAccountantReplacementReplyAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (!IsAccountantLearningManager(callback.From.Username))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این تنظیم فقط برای مدیر تعیین‌شده قابل تغییر است.",
                cancellationToken: cancellationToken);
            return;
        }

        var setting = await GetOrCreateAccountantLearningSettingAsync(callback.From.Id, cancellationToken);
        var backlogSince=setting.UpdatedAt??setting.CollectionStartedAt??DateTime.UtcNow.AddHours(-24);
        setting.IsAutoReplyEnabled = !setting.IsAutoReplyEnabled;
        setting.UpdatedAt = DateTime.UtcNow;
        if(setting.IsAutoReplyEnabled)
            _db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=Guid.Empty,Channel="AccountantReplyControl",EventType="Activated",
                Recipient=callback.From.Id.ToString(CultureInfo.InvariantCulture),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow,
                Payload=System.Text.Json.JsonSerializer.Serialize(new{SettingId=setting.Id,SinceUtc=backlogSince})});
        setting.UpdatedByTelegramUserId = callback.From.Id.ToString(CultureInfo.InvariantCulture);
        await _db.SaveChangesAsync(cancellationToken);
        await _sender.AnswerCallbackAsync(
            callback.Id,
            setting.IsAutoReplyEnabled
                ? "پاسخ جایگزین فعال شد؛ پیام‌های بی‌پاسخ ۲۴ ساعت اخیر بررسی می‌شوند."
                : "پاسخ‌گویی جایگزین حسابدار غیرفعال شد.",
            cancellationToken: cancellationToken);
        await SendAccountantAssistantSettingsAsync(callback.Message!.Chat.Id, callback.From, cancellationToken);
    }

    private async Task<TelegramAccountantLearningSetting> GetOrCreateAccountantLearningSettingAsync(
        long actorUserId,
        CancellationToken cancellationToken)
    {
        var setting = await _db.TelegramAccountantLearningSettings
            .Where(value=>!value.IsDeleted)
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (setting is not null)
            return setting;

        setting = new TelegramAccountantLearningSetting
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            IsLearningEnabled = true,
            IsAutoReplyEnabled = false,
            CollectionStartedAt = DateTime.UtcNow,
            UpdatedByTelegramUserId = actorUserId.ToString(CultureInfo.InvariantCulture)
        };
        _db.TelegramAccountantLearningSettings.Add(setting);
        return setting;
    }

    private async Task<(bool IsLearningEnabled, bool IsAutoReplyEnabled, int SampleCount, DateTime? StartedAt)>
        GetAccountantLearningStatusAsync(
        CancellationToken cancellationToken)
    {
        var setting = await _db.TelegramAccountantLearningSettings
            .AsNoTracking()
            .Where(value=>!value.IsDeleted)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => new
            {
                value.IsLearningEnabled,
                value.IsAutoReplyEnabled,
                value.CollectionStartedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
        var count = await _db.TelegramAccountantLearningMessages.AsNoTracking()
            .CountAsync(value => value.IsAccountant, cancellationToken);
        return (
            setting?.IsLearningEnabled ?? true,
            setting?.IsAutoReplyEnabled ?? false,
            count,
            setting?.CollectionStartedAt);
    }

    private async Task<bool> IsAccountantReplacementReplyEnabledAsync(CancellationToken cancellationToken) =>
        await _db.TelegramAccountantLearningSettings.AsNoTracking()
            .Where(value=>!value.IsDeleted)
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => (bool?)value.IsAutoReplyEnabled)
            .FirstOrDefaultAsync(cancellationToken) ?? false;

    private async Task SendAccountantAssistantSettingsAsync(
        long chatId,
        TelegramUser actor,
        CancellationToken cancellationToken)
    {
        if (!IsAccountantLearningManager(actor.Username))
        {
            await ReplyAsync(chatId, "این بخش فقط برای مدیر تعیین‌شده قابل مشاهده است.", cancellationToken);
            return;
        }

        var status = await GetAccountantLearningStatusAsync(cancellationToken);
        var message =
            "🤖 مدیریت دستیار جایگزین حسابدار\n\n" +
            $"👤 حسابدار مرجع: @AccountantZibashe\n" +
            $"🧠 جمع‌آوری و یادگیری: {(status.IsLearningEnabled ? "فعال ✅" : "غیرفعال ⛔")}\n" +
            $"💬 پاسخ‌گویی جایگزین حسابدار: {(status.IsAutoReplyEnabled ? "فعال ✅" : "غیرفعال ⛔")}\n" +
            $"📝 پاسخ‌های حسابدار ثبت‌شده: {status.SampleCount}\n\n" +
            "با شروع پاسخ جایگزین، پرسش‌های ذخیره‌شده و بی‌پاسخ ۲۴ ساعت اخیر بررسی می‌شوند؛ پیام‌های جدید گروه‌های حسابداری بدون تگ دریافت و بررسی می‌شوند. ربات به علامت unread تلگرام یا تاریخچهٔ دریافت‌نشده دسترسی ندارد.\n\n" +
            "پاسخ وضعیت عطر و پیشنهاد عطر مستقل از این تنظیمات همیشه فعال است. " +
            "پرسش‌های مالی و موارد نامطمئن نیز حتی در حالت جایگزین پاسخ داده نمی‌شوند.";
        IReadOnlyCollection<TelegramInlineButton>[] buttons =
        {
            new[]
            {
                new TelegramInlineButton(
                    status.IsLearningEnabled ? "⛔ توقف یادگیری" : "✅ شروع یادگیری",
                    "invoiceadmin:assistant:toggle-learning")
            },
            new[]
            {
                new TelegramInlineButton(
                    status.IsAutoReplyEnabled ? "⛔ توقف پاسخ جایگزین" : "✅ شروع پاسخ جایگزین",
                    "invoiceadmin:assistant:toggle-replies")
            },
            new[] { new TelegramInlineButton("↩️ بازگشت به منوی اصلی", "invoiceadmin:menu:main") }
        };
        await _sender.SendInlineKeyboardAsync(chatId.ToString(CultureInfo.InvariantCulture), message, buttons,
            cancellationToken);
    }

    private static bool IsAccountantLearningManager(string? username) =>
        NormalizeLearningUsername(username) == LearningManagerUsername;

    private static string NormalizeLearningUsername(string? username) =>
        username?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty;

    private static bool LooksLikeAccountantReplacementQuestion(string text)
    {
        var normalized = NormalizeCustomerQuestion(text);
        return text.Contains('?') || text.Contains('؟') ||
               new[] { "میشه", "می شود", "میتونید", "می تونید", "میتونی", "میخوام", "می خواهم", "چطور", "چگونه", "چرا", "کجا", "کی", "چه", "لطفا", "لطفاً" }
                   .Any(value => normalized.Contains(value, StringComparison.Ordinal));
    }

    private static HashSet<string> LearningTokens(string text) =>
        NormalizeCustomerQuestion(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);

    private static string? FirstTelegramMessageText(TelegramMessage message) =>
        string.IsNullOrWhiteSpace(message.Text) ? message.Caption : message.Text;

    private static bool ContainsSensitiveAccountantLearningData(string value) =>
        FinancialSubjects.Any(subject=>NormalizeCustomerQuestion(value).Contains(subject,StringComparison.Ordinal)) ||
        SensitiveLearningNumberPattern.IsMatch(value);

    private static string? TruncateLearningValue(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
