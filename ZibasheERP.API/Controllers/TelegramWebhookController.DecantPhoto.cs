using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string DecantPhotoDeliveryEvent = "DecantPhotoDelivery";
    private const string DecantPhotoWaitingEvent = "DecantPhotoWaitingForGroup";

    private async Task<bool> TryHandleDecantPhotoCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            !callback.Data.StartsWith("decantphoto:", StringComparison.Ordinal))
            return false;

        if (!await IsAuthorizedDecantPhotoAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", ct);
            return true;
        }

        if (callback.Data.StartsWith("decantphoto:retry:", StringComparison.Ordinal))
        {
            var idText = callback.Data["decantphoto:retry:".Length..];
            if (!Guid.TryParseExact(idText, "N", out var notificationId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "شناسه ارسال نامعتبر است.", ct);
                return true;
            }
            var notification = await _db.NotificationOutbox.FirstOrDefaultAsync(value =>
                value.Id == notificationId && !value.IsDeleted &&
                value.EventType == DecantPhotoDeliveryEvent,
                ct);
            if (notification is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "ارسال موردنظر پیدا نشد.", ct);
                return true;
            }
            var group = await _db.CustomerTelegramGroups.AsNoTracking().FirstOrDefaultAsync(value =>
                value.CustomerId == notification.CustomerId && !value.IsDeleted && value.IsActive,
                ct);
            if (group is null)
            {
                await _sender.AnswerCallbackAsync(
                    callback.Id,
                    "گروه فعال مشتری هنوز متصل نیست.",
                    ct,
                    showAlert: true);
                return true;
            }
            notification.Recipient = group.ChatId;
            notification.Status = NotificationOutboxStatus.Pending;
            notification.Attempts = 0;
            notification.LastError = null;
            notification.LockedUntil = null;
            notification.NextAttemptAt = DateTime.UtcNow;
            notification.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, "ارسال مجدد در صف قرار گرفت ✅", ct);
            return true;
        }

        if (callback.Data == "decantphoto:start")
        {
            _decantPhotoDrafts.Set(new TelegramDecantPhotoDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(
                callback.Message.Chat.Id,
                "📸 عکس دکانت را با کیفیت موردنظر ارسال کنید.",
                ct);
            return true;
        }

        if (callback.Data == "decantphoto:cancel")
        {
            _decantPhotoDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "لغو شد.", ct);
            await SendInvoiceAdminMenuAsync(callback.Message.Chat.Id, null, ct);
            return true;
        }

        if (callback.Data != "decantphoto:confirm" ||
            !_decantPhotoDrafts.TryGet(callback.Message.Chat.Id, callback.From.Id, out var draft) ||
            draft.Stage != TelegramDecantPhotoStage.AwaitingConfirmation)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است؛ دوباره شروع کنید.", ct);
            return true;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "در حال ساخت صف ارسال…", ct);
        var result = await QueueDecantPhotoDeliveriesAsync(draft, ct);
        _decantPhotoDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
        await SendInvoiceAdminMenuAsync(
            callback.Message.Chat.Id,
            $"✅ صف عکس دکانت ساخته شد.\n" +
            $"آماده ارسال: {result.Ready}\n" +
            $"در انتظار اتصال گروه: {result.Waiting}\n" +
            $"گیرنده پیدا نشد: {result.Unmatched}",
            ct);
        return true;
    }

    private async Task<bool> TryHandleDecantPhotoMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null ||
            !_decantPhotoDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _decantPhotoDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }

        if (draft.Stage == TelegramDecantPhotoStage.AwaitingPhoto)
        {
            var photo = message.Photo?
                .OrderByDescending(value => value.FileSize ?? (long)value.Width * value.Height)
                .FirstOrDefault();
            if (photo is null)
            {
                await ReplyAsync(message.Chat.Id, "لطفاً عکس دکانت را به‌صورت Photo ارسال کنید.", ct);
                return true;
            }

            draft.PhotoFileId = photo.FileId;
            draft.Stage = TelegramDecantPhotoStage.AwaitingSalesList;
            _decantPhotoDrafts.Set(draft);
            await ReplyAsync(
                message.Chat.Id,
                "عکس دریافت شد ✅\nحالا پیام لیست دکانت را فوروارد کنید یا کد لیست را بفرستید؛ مثال: 16716",
                ct);
            return true;
        }

        if (draft.Stage != TelegramDecantPhotoStage.AwaitingSalesList)
        {
            await ReplyAsync(message.Chat.Id, "از دکمه‌های تأیید یا لغو استفاده کنید.", ct);
            return true;
        }

        var rosterText = message.Text ?? message.Caption;
        var listCode = ParseSalesListCode(rosterText);
        if (!listCode.HasValue)
        {
            await ReplyAsync(message.Chat.Id, "کد لیست از پیام پیدا نشد. فقط عدد کد را بفرستید؛ مثال: 16716", ct);
            return true;
        }

        var list = await _db.SalesLists.AsNoTracking().FirstOrDefaultAsync(
            value => value.PublicCode == listCode.Value && !value.IsDeleted,
            ct);
        IReadOnlyList<DecantTarget> targets;
        if (list is null)
        {
            var legacyIdentities = ExtractLegacyDecantRecipients(rosterText);
            if (legacyIdentities.Count == 0)
            {
                await ReplyAsync(
                    message.Chat.Id,
                    $"لیست {listCode.Value} در دیتابیس جدید نیست و از متن هم آیدی قابل‌ارسال پیدا نشد. متن کامل لیست را فوروارد کنید.",
                    ct);
                return true;
            }
            targets = await ResolveDecantTargetsAsync(legacyIdentities, ct);
            draft.SalesListId = Guid.Empty;
            draft.PublicCode = listCode.Value;
            draft.SalesListName = $"لیست قدیمی {listCode.Value}";
            draft.LegacyRosterText = rosterText;
        }
        else
        {
            targets = await ResolveDecantTargetsAsync(list.Id, ct);
            draft.SalesListId = list.Id;
            draft.PublicCode = list.PublicCode;
            draft.SalesListName = string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName;
            draft.LegacyRosterText = null;
        }
        if (targets.Count == 0)
        {
            await ReplyAsync(message.Chat.Id, "این لیست گیرندهٔ قابل استخراج ندارد.", ct);
            return true;
        }

        draft.Stage = TelegramDecantPhotoStage.AwaitingConfirmation;
        _decantPhotoDrafts.Set(draft);
        var matched = targets.Count(value => value.CustomerId.HasValue);
        var ready = targets.Count(value => value.ActiveGroupChatId is not null);
        await _sender.SendPhotoWithKeyboardAsync(
            message.Chat.Id.ToString(),
            draft.PhotoFileId,
            $"پیش‌نمایش ارسال عکس دکانت\n{draft.SalesListName}\n\n" +
            $"گیرندگان یکتا: {targets.Count}\nآماده ارسال: {ready}\n" +
            $"در انتظار گروه: {matched - ready}\nپیدا نشده در مشتریان: {targets.Count - matched}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✅ تأیید و ساخت صف", "decantphoto:confirm") },
                new[] { new TelegramInlineButton("❌ لغو", "decantphoto:cancel") }
            },
            ct);
        return true;
    }

    private async Task<bool> IsAuthorizedDecantPhotoAdminAsync(
        long chatId,
        long userId,
        CancellationToken ct)
    {
        if (await IsAuthorizedInvoiceAdminAsync(chatId, userId, ct))
            return true;
        if (!long.TryParse(DecantFailureChatId(), out var failureChatId) || chatId != failureChatId)
            return false;
        if (IsPrimaryOwner(userId))
            return true;
        return long.TryParse(_options.AdminChatId, out var adminChatId) &&
            await _sender.IsChatAdministratorAsync(adminChatId.ToString(), userId.ToString(), ct);
    }

    private async Task<DecantQueueResult> QueueDecantPhotoDeliveriesAsync(
        TelegramDecantPhotoDraft draft,
        CancellationToken ct)
    {
        var targets = draft.SalesListId == Guid.Empty
            ? await ResolveDecantTargetsAsync(ExtractLegacyDecantRecipients(draft.LegacyRosterText), ct)
            : await ResolveDecantTargetsAsync(draft.SalesListId, ct);
        var ready = 0;
        var waiting = 0;
        var unmatched = 0;
        var now = DateTime.UtcNow;
        var caption = $"📸 عکس دکانت\n{draft.SalesListName}";

        foreach (var target in targets)
        {
            if (!target.CustomerId.HasValue)
            {
                unmatched++;
                await SendDecantFailureAlertAsync(
                    $"⚠️ گیرنده عکس دکانت در مشتریان پیدا نشد.\nعطر: {draft.SalesListName}\nگیرنده: {target.DisplayIdentity}",
                    target.CopyIdentity,
                    ct);
                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                draft.SalesListId,
                draft.PublicCode,
                SalesListName = draft.SalesListName,
                FileId = draft.PhotoFileId,
                Caption = caption,
                TargetIdentity = target.DisplayIdentity
            });
            var existing = await _db.NotificationOutbox.AsNoTracking().AnyAsync(value =>
                !value.IsDeleted && value.CustomerId == target.CustomerId.Value &&
                value.EventType == DecantPhotoDeliveryEvent && value.Payload == payload,
                ct);
            if (existing)
                continue;

            var isReady = target.ActiveGroupChatId is not null;
            _db.NotificationOutbox.Add(new NotificationOutbox
            {
                Id = Guid.NewGuid(),
                CreatedAt = now.AddTicks(ready + waiting),
                CustomerId = target.CustomerId.Value,
                Channel = "Telegram",
                EventType = DecantPhotoDeliveryEvent,
                Recipient = target.ActiveGroupChatId ?? string.Empty,
                Payload = payload,
                Status = isReady ? NotificationOutboxStatus.Pending : NotificationOutboxStatus.Failed,
                LastError = isReady ? null : "در انتظار اتصال گروه مشتری"
            });
            if (isReady)
                ready++;
            else
            {
                waiting++;
                _db.NotificationOutbox.Add(CreateDecantAdminAlert(
                    target.CustomerId.Value,
                    DecantPhotoWaitingEvent,
                    $"⏳ عکس دکانت در انتظار اتصال گروه است.\nعطر: {draft.SalesListName}\nگیرنده: {target.DisplayIdentity}",
                    target.CopyIdentity,
                    now.AddTicks(ready + waiting)));
            }
        }

        await _db.SaveChangesAsync(ct);
        return new DecantQueueResult(ready, waiting, unmatched);
    }

    private async Task<IReadOnlyList<DecantTarget>> ResolveDecantTargetsAsync(
        Guid salesListId,
        CancellationToken ct)
    {
        var requests = await _db.SalesListRequests.AsNoTracking()
            .Where(value => value.SalesListId == salesListId && !value.IsDeleted &&
                value.Kind == SalesListRequestKind.CurrentBottle &&
                (value.Status == SalesListRequestStatus.Confirmed ||
                 value.Status == SalesListRequestStatus.Promoted ||
                 value.Status == SalesListRequestStatus.QueuedForInvoice ||
                 value.Status == SalesListRequestStatus.Invoiced))
            .Select(value => new
            {
                Username = value.IsGift
                    ? value.GiftRecipientTelegramUsername
                    : value.TelegramUsername,
                TelegramId = value.IsGift
                    ? value.GiftRecipientTelegramUserId
                    : value.TelegramUserId
            })
            .ToArrayAsync(ct);
        return await ResolveDecantTargetsAsync(
            requests.Select(value => new DecantIdentity(value.Username, value.TelegramId)),
            ct);
    }

    private async Task<IReadOnlyList<DecantTarget>> ResolveDecantTargetsAsync(
        IEnumerable<DecantIdentity> sourceIdentities,
        CancellationToken ct)
    {
        var identities = sourceIdentities
            .Select(value => new DecantIdentity(
                NormalizeDecantUsername(value.Username),
                NormalizeDecantTelegramId(value.TelegramId)))
            .Where(value => value.Username is not null || value.TelegramId is not null)
            .GroupBy(value => value.Username is not null
                ? "u:" + value.Username.ToLowerInvariant()
                : "i:" + value.TelegramId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (identities.Length == 0)
            return [];

        var usernames = identities.Where(value => value.Username is not null)
            .Select(value => value.Username!.ToLower()).ToArray();
        var telegramIds = identities.Where(value => value.TelegramId is not null)
            .Select(value => value.TelegramId!).ToArray();
        var customers = await _db.Customers.AsNoTracking()
            .Include(value => value.TelegramGroup)
            .Where(value => !value.IsDeleted &&
                ((value.Username != null && usernames.Contains(value.Username.ToLower())) ||
                 (value.TelegramId != null && telegramIds.Contains(value.TelegramId))))
            .ToArrayAsync(ct);

        return identities.Select(identity =>
        {
            var customer = customers.FirstOrDefault(value =>
                identity.Username is not null && value.Username != null &&
                string.Equals(value.Username.TrimStart('@'), identity.Username, StringComparison.OrdinalIgnoreCase))
                ?? customers.FirstOrDefault(value => identity.TelegramId is not null &&
                    value.TelegramId == identity.TelegramId);
            var display = identity.Username is not null ? "@" + identity.Username : identity.TelegramId!;
            return new DecantTarget(
                customer?.Id,
                customer?.TelegramGroup is { IsActive: true, IsDeleted: false }
                    ? customer.TelegramGroup.ChatId
                    : null,
                display,
                display);
        }).ToArray();
    }

    private static IReadOnlyList<DecantIdentity> ExtractLegacyDecantRecipients(string? rosterText)
    {
        if (string.IsNullOrWhiteSpace(rosterText))
            return [];
        var beforeNextBottle = new Regex(@"^\s*next\s+bottle\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Split(rosterText, 2)[0];
        var matches = Regex.Matches(
            beforeNextBottle,
            @"(?im)^\s*@(?<sender>[A-Za-z0-9_]{5,})\b(?:\s+for\s+@(?<recipient>[A-Za-z0-9_]{5,})\b)?");
        return matches.Select(match => new DecantIdentity(
                match.Groups["recipient"].Success
                    ? match.Groups["recipient"].Value
                    : match.Groups["sender"].Value,
                null))
            .ToArray();
    }

    private NotificationOutbox CreateDecantAdminAlert(
        Guid customerId,
        string eventType,
        string message,
        string copyIdentity,
        DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            CustomerId = customerId,
            Channel = "Telegram",
            EventType = eventType,
            Recipient = DecantFailureChatId(),
            Payload = JsonSerializer.Serialize(new { Message = message, CopyIdentity = copyIdentity })
        };

    private async Task SendDecantFailureAlertAsync(string message, string copyIdentity, CancellationToken ct)
    {
        var chatId = DecantFailureChatId();
        if (string.IsNullOrWhiteSpace(chatId))
        {
            _logger.LogWarning("Decant photo failure chat is not configured: {Message}", message);
            return;
        }
        await _sender.SendInlineKeyboardAsync(
            chatId,
            message,
            new[]
            {
                (IReadOnlyCollection<TelegramInlineButton>)new[]
                {
                    new TelegramInlineButton("📋 کپی آیدی", CopyText: copyIdentity)
                }
            },
            ct);
    }

    private string DecantFailureChatId() => string.IsNullOrWhiteSpace(_options.DecantPhotoFailureChatId)
        ? _options.InvoiceFailureChatId.Trim()
        : _options.DecantPhotoFailureChatId.Trim();

    private static int? ParseSalesListCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var normalized = text.Trim();
        if (int.TryParse(normalized, out var direct))
            return direct;
        var match = Regex.Match(normalized, @"کد\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private static string? NormalizeDecantUsername(string? value)
    {
        var normalized = value?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeDecantTelegramId(string? value)
    {
        var normalized = value?.Trim();
        return long.TryParse(normalized, out _) ? normalized : null;
    }

    private sealed record DecantTarget(
        Guid? CustomerId,
        string? ActiveGroupChatId,
        string DisplayIdentity,
        string CopyIdentity);

    private sealed record DecantIdentity(string? Username, string? TelegramId);

    private sealed record DecantQueueResult(int Ready, int Waiting, int Unmatched);
}
