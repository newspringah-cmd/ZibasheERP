using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed class BlockedUsernameDraft
    {
        public string? NormalizedUsername { get; set; }
        public bool WasBlocked { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<(long ChatId, long UserId), BlockedUsernameDraft>
        BlockedUsernameDrafts = new();

    private async Task HandleBlockedUsernameCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var key = (chatId, callback.From.Id);
        if (callback.Data == "invoiceadmin:block-user")
        {
            ClearAdminWorkflowDrafts(chatId, callback.From.Id);
            BlockedUsernameDrafts[key] = new BlockedUsernameDraft();
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId, "یوزرنیم را برای بلاک یا رفع بلاک وارد کنید؛ مثال: @username", ct);
            return;
        }

        if (callback.Data == "invoiceadmin:block-user:cancel")
        {
            BlockedUsernameDrafts.TryRemove(key, out _);
            await _sender.AnswerCallbackAsync(callback.Id, "لغو شد.", ct);
            await SendInvoiceAdminSectionAsync(chatId, "items", callback.From.Id, ct);
            return;
        }

        if (callback.Data != "invoiceadmin:block-user:confirm" ||
            !BlockedUsernameDrafts.TryGetValue(key, out var draft) ||
            string.IsNullOrWhiteSpace(draft.NormalizedUsername))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
            return;
        }

        var record = await _db.TelegramBlockedUsernames
            .SingleOrDefaultAsync(value => value.NormalizedUsername == draft.NormalizedUsername, ct);
        var currentlyBlocked = record is { IsDeleted: false };
        if (currentlyBlocked != draft.WasBlocked)
        {
            BlockedUsernameDrafts.TryRemove(key, out _);
            await _sender.AnswerCallbackAsync(callback.Id,
                "وضعیت هم‌زمان تغییر کرده است؛ دوباره تلاش کنید.", ct, true);
            return;
        }
        var now = DateTime.UtcNow;
        var shouldBlock = record is null || record.IsDeleted;
        if (record is null)
        {
            _db.TelegramBlockedUsernames.Add(new TelegramBlockedUsername
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                NormalizedUsername = draft.NormalizedUsername,
                BlockedByTelegramUserId = callback.From.Id.ToString()
            });
        }
        else
        {
            record.IsDeleted = !shouldBlock;
            record.UpdatedAt = now;
            record.BlockedByTelegramUserId = callback.From.Id.ToString();
        }

        await _db.SaveChangesAsync(ct);
        BlockedUsernameDrafts.TryRemove(key, out _);
        var status = shouldBlock ? "بلاک شد 🚫" : "از بلاک خارج شد ✅";
        await _sender.AnswerCallbackAsync(callback.Id, status, ct, true);
        await ReplyAsync(chatId, $"@{draft.NormalizedUsername} برای ثبت آیتم {status}", ct);
    }

    private async Task<bool> TryHandleBlockedUsernameMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null ||
            !BlockedUsernameDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            BlockedUsernameDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            return false;
        }

        var input = message.Text?.Trim();
        if (string.Equals(input, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            BlockedUsernameDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "عملیات لغو شد.", ct);
            return true;
        }
        if (input?.StartsWith('/') == true)
            return false;

        if (string.IsNullOrWhiteSpace(input) || !TryNormalizeBlockableUsername(input, out var username))
        {
            await ReplyAsync(message.Chat.Id,
                "یوزرنیم معتبر وارد کنید؛ فقط حروف انگلیسی، عدد و زیرخط، بین ۵ تا ۳۲ کاراکتر. مثال: @username", ct);
            return true;
        }

        var isBlocked = await _db.TelegramBlockedUsernames.AsNoTracking()
            .AnyAsync(value => !value.IsDeleted && value.NormalizedUsername == username, ct);
        draft.NormalizedUsername = username;
        draft.WasBlocked = isBlocked;
        draft.UpdatedAt = DateTime.UtcNow;
        BlockedUsernameDrafts[(message.Chat.Id, message.From.Id)] = draft;
        var action = isBlocked ? "رفع بلاک" : "بلاک";
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
            $"یوزرنیم: @{username}\nوضعیت فعلی: {(isBlocked ? "مسدود" : "آزاد")}\n\n{action} انجام شود؟",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton($"✅ تأیید {action}", "invoiceadmin:block-user:confirm") },
                new[] { new TelegramInlineButton("❌ انصراف", "invoiceadmin:block-user:cancel") }
            }, ct);
        return true;
    }

    private async Task EnsureUsernameCanRegisterAsync(string? username, CancellationToken ct)
    {
        if (!TryNormalizeBlockableUsername(username, out var normalized))
            throw new InvalidOperationException(
                "جهت ثبت ایتم لطفا یوزرنیم خود رو در تنظیمات تلگرام ثبت کنید در صورت نیاز به راهنمایی به ادمین پیام بدید");
        if (await _db.TelegramBlockedUsernames.AsNoTracking()
                .AnyAsync(value => !value.IsDeleted && value.NormalizedUsername == normalized, ct))
            throw new InvalidOperationException("امکان ثبت آیتم برای این حساب مسدود شده است. با ادمین تماس بگیرید.");
    }

    private static bool TryNormalizeBlockableUsername(string? value, out string normalized)
    {
        normalized = value?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty;
        return Regex.IsMatch(normalized, "^[a-z][a-z0-9_]{4,31}$", RegexOptions.CultureInvariant);
    }
}
