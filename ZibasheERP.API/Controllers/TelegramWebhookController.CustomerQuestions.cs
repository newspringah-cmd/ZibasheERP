using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.CustomerAssistant;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static readonly string[] CustomerStatusSubjects =
    [
        "عطر", "ادکلن", "دکانت", "سفارش", "لیست", "ایتم", "آیتم",
        "خریدم", "مرسوله"
    ];

    private static readonly string[] CustomerStatusQuestions =
    [
        "کی", "چه زمان", "چه موقع", "وضعیت", "کجاست", "چی شد", "چی شده",
        "چه مرحله", "میشه", "می شود", "آیا"
    ];

    private static readonly HashSet<string> PerfumeMatchStopWords = new(StringComparer.Ordinal)
    {
        "عطر", "ادکلن", "ادوپرفیوم", "ادوتویلت", "پرفیوم", "برای", "این", "اون",
        "های", "من", "لیست", "سفارش", "دکانت"
    };

    private static readonly string[] FinancialSubjects =
    [
        "پرداخت", "فاکتور", "تسویه", "بدهی", "واریز", "کارت به کارت", "رسید پرداخت",
        "مبلغ فاکتور", "شماره کارت"
    ];

    private static readonly string[] PerfumeGuidanceSubjects =
    [
        "عطر", "ادکلن", "رایحه", "نت", "آکورد", "بو", "پخش بو", "ماندگاری",
        "چوبی", "گلی", "مرکباتی", "وانیلی", "پودری", "چرمی", "دودی", "میوه ای",
        "شیرین", "تلخ", "گرم", "خنک", "تند", "ملایم", "زنانه", "مردانه", "یونیسکس"
    ];

    private static readonly string[] PerfumeGuidanceCues =
    [
        "پیشنهاد", "معرفی", "مناسب", "دنبال", "میخوام", "می خواهم", "دوست دارم",
        "چی", "کدوم", "کدام", "چه عطری", "راهنمایی", "بهتره", "انتخاب"
    ];

    private async Task<bool> TryHandleCustomerStatusQuestionAsync(
        TelegramMessage message,
        CancellationToken cancellationToken)
    {
        var isFinancialQuestion = IsFinancialQuestion(message.Text);
        if (!isFinancialQuestion && !IsCustomerStatusQuestion(message.Text))
            return false;

        var group = await _db.CustomerTelegramGroups
            .AsNoTracking()
            .Where(value =>
                !value.IsDeleted &&
                value.IsActive &&
                value.ChatId == message.Chat.Id.ToString())
            .Select(value => new
            {
                value.CustomerId,
                value.Customer.TelegramId,
                value.Customer.Username
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null)
            return false;

        if (isFinancialQuestion)
        {
            await ReplyToCustomerQuestionAsync(
                message,
                "برای بررسی پرداخت، فاکتور، تسویه و سایر مسائل مالی لطفاً با حسابداری زیباشی در ارتباط باشید.",
                cancellationToken);
            return true;
        }

        var rows = await _db.OrderItems
            .AsNoTracking()
            .Where(item =>
                !item.IsDeleted &&
                item.Order != null &&
                !item.Order.IsDeleted &&
                item.Order.CustomerId == group.CustomerId &&
                item.Order.Status != OrderStatus.Cancelled)
            .OrderByDescending(item => item.UpdatedAt ?? item.CreatedAt)
            .Take(150)
            .Select(item => new CustomerItemStatusRow(
                item.Id,
                item.SalesListId,
                item.PerfumeId,
                item.SalesList != null ? item.SalesList.PublicCode : null,
                item.SalesList != null ? item.SalesList.PersianName : null,
                item.SalesList != null ? item.SalesList.EnglishName : null,
                item.Perfume != null ? item.Perfume.Name : null,
                item.Perfume != null ? item.Perfume.EnglishName : null,
                item.ManualDescription,
                item.FulfillmentStatus,
                item.UpdatedAt ?? item.CreatedAt))
            .ToListAsync(cancellationToken);

        var customerUsername = group.Username?.Trim().TrimStart('@');
        var requestRows = await _db.SalesListRequests
            .AsNoTracking()
            .Where(request =>
                !request.IsDeleted &&
                request.SalesList.Status != SalesListStatus.Cancelled &&
                request.Status != SalesListRequestStatus.PendingConfirmation &&
                request.Status != SalesListRequestStatus.Cancelled &&
                request.Status != SalesListRequestStatus.Expired &&
                ((!string.IsNullOrWhiteSpace(group.TelegramId) &&
                  request.TelegramUserId == group.TelegramId) ||
                 (!string.IsNullOrWhiteSpace(customerUsername) &&
                  request.TelegramUsername == customerUsername) ||
                 (request.IsGift &&
                  ((!string.IsNullOrWhiteSpace(group.TelegramId) &&
                    request.GiftRecipientTelegramUserId == group.TelegramId) ||
                   (!string.IsNullOrWhiteSpace(customerUsername) &&
                    request.GiftRecipientTelegramUsername == customerUsername)))))
            .OrderByDescending(request => request.UpdatedAt ?? request.CreatedAt)
            .Take(150)
            .Select(request => new
            {
                request.Id,
                request.SalesListId,
                request.SalesList.PerfumeId,
                request.SalesList.PublicCode,
                request.SalesList.PersianName,
                request.SalesList.EnglishName,
                PerfumeName = request.SalesList.Perfume.Name,
                PerfumeEnglishName = request.SalesList.Perfume.EnglishName,
                request.SalesList.Status,
                ChangedAt = request.UpdatedAt ?? request.CreatedAt
            })
            .ToListAsync(cancellationToken);
        rows.AddRange(requestRows.Select(request => new CustomerItemStatusRow(
            request.Id,
            request.SalesListId,
            request.PerfumeId,
            request.PublicCode,
            request.PersianName,
            request.EnglishName,
            request.PerfumeName,
            request.PerfumeEnglishName,
            null,
            SalesListFulfillmentStatus(request.Status),
            request.ChangedAt)));

        if (rows.Count == 0)
        {
            await ReplyToCustomerQuestionAsync(
                message,
                "برای این حساب هنوز آیتم سفارشی ثبت‌شده‌ای پیدا نکردم.",
                cancellationToken);
            return true;
        }

        var products = rows
            .GroupBy(ItemIdentity)
            .Select(grouping => grouping
                .OrderByDescending(value => value.Status)
                .ThenByDescending(value => value.ChangedAt)
                .First())
            .OrderBy(value => value.Status == OrderItemFulfillmentStatus.Shipped)
            .ThenByDescending(value => value.ChangedAt)
            .ToList();

        var contextText = string.Join(' ', new[]
        {
            message.Text,
            message.ReplyToMessage?.Text,
            message.ReplyToMessage?.Caption
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var normalizedContext = NormalizeCustomerQuestion(contextText);
        var requestedCodes = Regex.Matches(normalizedContext, @"(?<!\d)\d{3,6}(?!\d)")
            .Select(value => value.Value)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        List<CustomerItemStatusRow> selected;
        selected = products
            .Where(value => value.PublicCode.HasValue &&
                            requestedCodes.Contains(value.PublicCode.Value.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        if (selected.Count > 0)
        {
            // An exact public-list code is more reliable than fuzzy perfume-name matching.
        }
        else
        {
            if (normalizedContext.Contains("کد لیست", StringComparison.Ordinal) && requestedCodes.Count > 0)
            {
                await ReplyToCustomerQuestionAsync(
                    message,
                    $"در سفارش‌های این حساب، آیتمی از لیست {requestedCodes.First()} پیدا نکردم.",
                    cancellationToken);
                return true;
            }

            var scored = products
                .Select(value => new
                {
                    Item = value,
                    Score = MatchPerfumeScore(value, normalizedContext)
                })
                .Where(value => value.Score > 0)
                .ToList();
            var bestScore = scored.Count == 0 ? 0 : scored.Max(value => value.Score);
            selected = scored
                .Where(value => value.Score == bestScore)
                .Select(value => value.Item)
                .ToList();

            if (selected.Count == 0)
            {
                var active = products
                    .Where(value => value.Status != OrderItemFulfillmentStatus.Shipped)
                    .Take(8)
                    .ToList();
                selected = active.Count > 0 ? active : products.Take(5).ToList();
            }
        }

        var asksForTime = normalizedContext.Contains("کی", StringComparison.Ordinal) ||
                          normalizedContext.Contains("چه زمان", StringComparison.Ordinal) ||
                          normalizedContext.Contains("چه موقع", StringComparison.Ordinal);
        var answer = FormatCustomerItemStatuses(selected, asksForTime);
        await ReplyToCustomerQuestionAsync(message, answer, cancellationToken);
        return true;
    }

    private async Task ReplyToCustomerQuestionAsync(
        TelegramMessage message,
        string answer,
        CancellationToken cancellationToken)
    {
        var signedAnswer = ZibaAssistantIdentity.Signature + "\n\n" + answer;
        var result = message.MessageId > 0
            ? await _sender.SendReplyAsync(
                message.Chat.Id.ToString(), signedAnswer, message.MessageId, cancellationToken)
            : await _sender.SendAsync(message.Chat.Id.ToString(), signedAnswer, cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram customer status answer failed: {Error}", result.Error);
    }

    private async Task<bool> TryHandlePerfumeGuidanceQuestionAsync(
        TelegramMessage message,
        CancellationToken cancellationToken)
    {
        var contextText = string.Join(' ', new[]
        {
            message.Text,
            message.ReplyToMessage?.Text,
            message.ReplyToMessage?.Caption
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!IsPerfumeGuidanceQuestion(contextText))
            return false;

        var isConnectedGroup = await _db.CustomerTelegramGroups
            .AsNoTracking()
            .AnyAsync(value =>
                !value.IsDeleted &&
                value.IsActive &&
                value.ChatId == message.Chat.Id.ToString(),
                cancellationToken);
        if (!isConnectedGroup)
            return false;

        var catalogRows = await _db.SalesLists
            .AsNoTracking()
            .Where(value =>
                !value.IsDeleted &&
                value.Status == SalesListStatus.Open &&
                value.Perfume.IsActive)
            .OrderByDescending(value => value.OpenDate)
            .Take(60)
            .Select(value => new PerfumeGuidanceCatalogRow(
                value.PublicCode,
                value.PersianName,
                value.EnglishName,
                value.DisplayBrand,
                value.Gender,
                value.TopNotes,
                value.MiddleNotes,
                value.BaseNotes,
                value.Accords,
                value.TotalVolume,
                value.ReservedVolume,
                value.TelegramPhotoFileId,
                value.ProductPageUrl,
                value.TelegramChannelId,
                value.TelegramMessageId))
            .ToListAsync(cancellationToken);
        var catalog = catalogRows
            .Select(value => new PerfumeRecommendationCatalogItem(
                value.PublicCode,
                value.PersianName,
                value.EnglishName,
                value.DisplayBrand,
                value.Gender == PerfumeGender.Women
                    ? "زنانه"
                    : value.Gender == PerfumeGender.Men
                        ? "مردانه"
                        : "یونیسکس",
                value.TopNotes,
                value.MiddleNotes,
                value.BaseNotes,
                value.Accords,
                Math.Max(0, value.TotalVolume - value.ReservedVolume)))
            .ToArray();

        var result = await _perfumeRecommendationService.RecommendAsync(
            contextText,
            catalog,
            cancellationToken);
        await ReplyToCustomerQuestionAsync(
            message,
            result.IsSuccessful
                ? result.Answer!
                : result.Error ?? "راهنمای انتخاب عطر فعلاً در دسترس نیست.",
            cancellationToken);
        if (result.IsSuccessful && result.ListCodes is { Count: > 0 })
            await SendRecommendedPerfumeCardsAsync(
                message.Chat.Id,
                result.ListCodes,
                catalogRows,
                cancellationToken);
        return true;
    }

    private async Task SendRecommendedPerfumeCardsAsync(
        long chatId,
        IReadOnlyCollection<int> listCodes,
        IReadOnlyCollection<PerfumeGuidanceCatalogRow> catalogRows,
        CancellationToken cancellationToken)
    {
        var rowsByCode = catalogRows
            .GroupBy(value => value.PublicCode)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var code in listCodes.Distinct().Take(3))
        {
            if (!rowsByCode.TryGetValue(code, out var row))
                continue;

            var persianName = string.IsNullOrWhiteSpace(row.PersianName)
                ? "عطر پیشنهادی"
                : row.PersianName;
            var englishName = row.EnglishName;
            var productUrl = ValidCustomerLink(row.ProductPageUrl);
            var listUrl = BuildCustomerListUrl(row.TelegramChannelId, row.TelegramMessageId);
            var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
            if (productUrl is not null)
                buttons.Add(new[] { new TelegramInlineButton("🔗 مشاهده مشخصات عطر", Url: productUrl) });
            if (listUrl is not null && !string.Equals(listUrl, productUrl, StringComparison.OrdinalIgnoreCase))
                buttons.Add(new[] { new TelegramInlineButton("🧴 مشاهده لیست زیباشی", Url: listUrl) });

            var caption = $"🧴 <b>{Html(persianName)}</b>" +
                          (string.IsNullOrWhiteSpace(englishName) ? string.Empty : $"\n{Html(englishName)}") +
                          $"\nکد لیست: {code}";
            TelegramSendResult sent;
            if (!string.IsNullOrWhiteSpace(row.TelegramPhotoFileId) && buttons.Count > 0)
            {
                sent = await _sender.SendPhotoWithKeyboardAsync(
                    chatId.ToString(),
                    row.TelegramPhotoFileId,
                    caption,
                    buttons,
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(row.TelegramPhotoFileId))
            {
                sent = await _sender.SendPhotoHtmlAsync(
                    chatId.ToString(),
                    row.TelegramPhotoFileId,
                    caption,
                    cancellationToken);
            }
            else if (buttons.Count > 0)
            {
                sent = await _sender.SendInlineKeyboardAsync(
                    chatId.ToString(),
                    $"🧴 {persianName}\nکد لیست: {code}",
                    buttons,
                    cancellationToken);
            }
            else
            {
                continue;
            }

            if (!sent.IsSuccessful)
                _logger.LogWarning(
                    "Telegram recommended perfume card failed for list {PublicCode}: {Error}",
                    code,
                    sent.Error);
        }
    }

    private static string? ValidCustomerLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.ToString()
            : null;

    private static string? BuildCustomerListUrl(string? channelId, long? messageId)
    {
        if (!messageId.HasValue || string.IsNullOrWhiteSpace(channelId))
            return null;
        var channel = channelId.Trim();
        if (channel.StartsWith("-100", StringComparison.Ordinal) && channel.Length > 4)
            return $"https://t.me/c/{channel[4..]}/{messageId.Value}";
        var username = channel.TrimStart('@');
        return username.Length > 0 && !long.TryParse(username, out _)
            ? $"https://t.me/{username}/{messageId.Value}"
            : null;
    }

    private static bool IsCustomerStatusQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 4 or > 500)
            return false;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('/'))
            return false;

        var normalized = NormalizeCustomerQuestion(text);
        var hasQuestionCue = text.Contains('؟') ||
                             text.Contains('?') ||
                             CustomerStatusQuestions.Any(value =>
                                 normalized.Contains(value, StringComparison.Ordinal));
        return hasQuestionCue &&
               CustomerStatusSubjects.Any(value => normalized.Contains(value, StringComparison.Ordinal));
    }

    private static bool IsFinancialQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 3 or > 500 || text.TrimStart().StartsWith('/'))
            return false;
        var normalized = NormalizeCustomerQuestion(text);
        var hasQuestionCue = text.Contains('؟') ||
                             text.Contains('?') ||
                             CustomerStatusQuestions.Any(value =>
                                 normalized.Contains(value, StringComparison.Ordinal));
        return hasQuestionCue &&
               FinancialSubjects.Any(value => normalized.Contains(value, StringComparison.Ordinal));
    }

    private static bool IsPerfumeGuidanceQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 4 or > 700 || text.TrimStart().StartsWith('/'))
            return false;

        var normalized = NormalizeCustomerQuestion(text);
        var subjectMatches = PerfumeGuidanceSubjects.Count(value =>
            normalized.Contains(value, StringComparison.Ordinal));
        var hasCue = text.Contains('؟') ||
                     text.Contains('?') ||
                     PerfumeGuidanceCues.Any(value =>
                         normalized.Contains(value, StringComparison.Ordinal));

        return (hasCue && subjectMatches >= 1) || subjectMatches >= 2;
    }

    private static string FormatCustomerItemStatuses(
        IReadOnlyCollection<CustomerItemStatusRow> items,
        bool asksForTime)
    {
        if (items.Count == 1)
        {
            var item = items.Single();
            var answer = $"وضعیت «{ItemDisplayName(item)}»{FormatListCode(item.PublicCode)}: " +
                $"{CustomerSafeStatusLabel(item.Status)}.";
            if (asksForTime && item.Status != OrderItemFulfillmentStatus.Shipped)
                answer += "\nزمان دقیق مرحله بعد در سیستم ثبت نشده؛ به‌محض تغییر، وضعیت جدید همین‌جا قابل بررسی است.";
            return answer;
        }

        var lines = items
            .Take(8)
            .Select(item =>
                $"• {ItemDisplayName(item)}{FormatListCode(item.PublicCode)} — " +
                CustomerSafeStatusLabel(item.Status));
        var response = "آخرین وضعیت آیتم‌های شما:\n" + string.Join("\n", lines);
        if (items.Count > 8)
            response += $"\nو {items.Count - 8} مورد دیگر";
        if (asksForTime && items.Any(value => value.Status != OrderItemFulfillmentStatus.Shipped))
            response += "\n\nزمان دقیق مرحله بعد در سیستم ثبت نشده. برای یک عطر مشخص، نام عطر یا کد لیست را بفرستید.";
        return response;
    }

    private static int MatchPerfumeScore(CustomerItemStatusRow item, string normalizedQuestion)
    {
        var names = new[]
        {
            item.SalesListPersianName,
            item.SalesListEnglishName,
            item.PerfumeName,
            item.PerfumeEnglishName,
            item.ManualDescription
        };
        var best = 0;
        foreach (var rawName in names.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var name = NormalizeCustomerQuestion(rawName!);
            if (name.Length >= 4 && normalizedQuestion.Contains(name, StringComparison.Ordinal))
                best = Math.Max(best, 100 + name.Length);

            var tokenScore = name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => token.Length >= 3 && !PerfumeMatchStopWords.Contains(token))
                .Where(token => normalizedQuestion.Contains(token, StringComparison.Ordinal))
                .Sum(token => token.Length);
            best = Math.Max(best, tokenScore);
        }
        return best;
    }

    private static string NormalizeCustomerQuestion(string value)
    {
        var decomposed = value
            .Replace('ي', 'ی')
            .Replace('ى', 'ی')
            .Replace('ك', 'ک')
            .Replace('\u200c', ' ')
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(ToLatinDigit(character));
                continue;
            }
            builder.Append(' ');
        }
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static char ToLatinDigit(char value) => value switch
    {
        >= '\u06F0' and <= '\u06F9' => (char)('0' + value - '\u06F0'),
        >= '\u0660' and <= '\u0669' => (char)('0' + value - '\u0660'),
        _ => value
    };

    private static string ItemIdentity(CustomerItemStatusRow item) =>
        item.SalesListId?.ToString("N") ??
        item.PerfumeId?.ToString("N") ??
        NormalizeCustomerQuestion(item.ManualDescription ?? item.Id.ToString("N"));

    private static string ItemDisplayName(CustomerItemStatusRow item) =>
        FirstNotBlank(
            item.SalesListPersianName,
            item.PerfumeName,
            item.ManualDescription,
            item.SalesListEnglishName,
            item.PerfumeEnglishName) ?? "آیتم سفارش";

    private static string? FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatListCode(int? publicCode) =>
        publicCode.HasValue ? $" (کد لیست {publicCode.Value})" : string.Empty;

    private static OrderItemFulfillmentStatus SalesListFulfillmentStatus(SalesListStatus status) => status switch
    {
        SalesListStatus.Open => OrderItemFulfillmentStatus.WaitingForListCompletion,
        SalesListStatus.Full => OrderItemFulfillmentStatus.ListCompleted,
        SalesListStatus.AwaitingAvailability => OrderItemFulfillmentStatus.AwaitingPurchase,
        SalesListStatus.Purchased => OrderItemFulfillmentStatus.Purchased,
        SalesListStatus.QueuedForInvoice => OrderItemFulfillmentStatus.ListCompleted,
        SalesListStatus.Invoiced or SalesListStatus.Closed => OrderItemFulfillmentStatus.Invoiced,
        _ => OrderItemFulfillmentStatus.WaitingForListCompletion
    };

    private static string CustomerSafeStatusLabel(OrderItemFulfillmentStatus status) => status switch
    {
        OrderItemFulfillmentStatus.Invoiced =>
            "برای بررسی وضعیت این مرحله لطفاً با حسابداری زیباشی در ارتباط باشید",
        _ => OrderItemFulfillmentStatusLabel(status)
    };

    private sealed record CustomerItemStatusRow(
        Guid Id,
        Guid? SalesListId,
        Guid? PerfumeId,
        int? PublicCode,
        string? SalesListPersianName,
        string? SalesListEnglishName,
        string? PerfumeName,
        string? PerfumeEnglishName,
        string? ManualDescription,
        OrderItemFulfillmentStatus Status,
        DateTime ChangedAt);

    private sealed record PerfumeGuidanceCatalogRow(
        int PublicCode,
        string PersianName,
        string EnglishName,
        string DisplayBrand,
        PerfumeGender Gender,
        string TopNotes,
        string MiddleNotes,
        string BaseNotes,
        string Accords,
        int TotalVolume,
        int ReservedVolume,
        string? TelegramPhotoFileId,
        string ProductPageUrl,
        string? TelegramChannelId,
        long? TelegramMessageId);
}
