using System.Globalization;
using MediatR;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string CombinedNotesPrompt =
        "نت‌ها را وارد کنید. برای عطر تک‌نت فقط یک خط بفرستید؛ برای عطر سه‌مرحله‌ای سه خط به‌ترتیب نت ابتدایی، میانی و پایانی بفرستید.";

    private static readonly TelegramAdminSalesListStage[] ExistingReviewStages =
    [
        TelegramAdminSalesListStage.AwaitingEnglishName,
        TelegramAdminSalesListStage.AwaitingProductPageUrl,
        TelegramAdminSalesListStage.AwaitingBrand,
        TelegramAdminSalesListStage.AwaitingGender,
        TelegramAdminSalesListStage.AwaitingReleaseYear,
        TelegramAdminSalesListStage.AwaitingPersianName,
        TelegramAdminSalesListStage.AwaitingTopNotes,
        TelegramAdminSalesListStage.AwaitingAccords,
        TelegramAdminSalesListStage.AwaitingPrice,
        TelegramAdminSalesListStage.AwaitingVolume,
        TelegramAdminSalesListStage.AwaitingMinimumVolume
    ];

    private static readonly string[] ExistingReviewLabels =
    [
        "نام انگلیسی", "لینک صفحه عطر", "برند", "جنسیت", "سال تولید",
        "نام فارسی", "نت‌ها", "آکوردهای اصلی", "قیمت هر میل", "حجم کل", "حداقل حجم درخواست"
    ];

    private static readonly string[] ExistingEditPrompts =
    [
        "نام انگلیسی جدید را وارد کنید.",
        "لینک کامل و جدید صفحه عطر را وارد کنید؛ باید با https:// شروع شود.",
        "نام جدید برند را به انگلیسی وارد کنید.",
        "جنسیت را وارد کنید: زنانه، مردانه یا یونیسکس",
        "سال تولید جدید را میلادی وارد کنید.",
        "نام فارسی جدید عطر را وارد کنید.",
        CombinedNotesPrompt,
        "آکوردهای اصلی جدید را به فارسی وارد کنید.",
        "قیمت جدید هر میل را به تومان وارد کنید.",
        "حجم کل لیست جدید را به میل وارد کنید.",
        "حداقل حجم قابل درخواست مشتری را وارد کنید."
    ];

    private async Task<bool> TryHandleAdminSalesListCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (callback.Message is null || string.IsNullOrWhiteSpace(callback.Data) ||
            !callback.Data.StartsWith("adminlist:", StringComparison.Ordinal))
            return false;

        var chatId = callback.Message.Chat.Id;
        var userId = callback.From.Id;
        if (!await IsAuthorizedInvoiceAdminAsync(chatId, userId, cancellationToken))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", cancellationToken);
            return true;
        }

        var parts = callback.Data.Split(':');
        switch (parts.ElementAtOrDefault(1))
        {
            case "new":
            case "restart":
                _adminSalesListDrafts.Remove(chatId, userId);
                await SendNewSalesListSourceMenuAsync(chatId, cancellationToken);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
                return true;

            case "newperfume":
                await StartNewPerfumeDraftAsync(callback, cancellationToken);
                return true;

            case "search":
                _adminSalesListDrafts.Set(new TelegramAdminSalesListDraft
                {
                    ChatId = chatId,
                    UserId = userId,
                    Stage = TelegramAdminSalesListStage.AwaitingPerfumeSearch
                });
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
                await ReplyAsync(chatId, "نام فارسی، نام انگلیسی یا برند عطر را برای جستجو وارد کنید.", cancellationToken);
                return true;

            case "perfume" when parts.Length == 3 && Guid.TryParseExact(parts[2], "N", out var perfumeId):
                await StartSalesListDraftAsync(callback, perfumeId, cancellationToken);
                return true;

            case "review" when parts.Length == 3 && parts[2] is "keep" or "edit":
                if (!_adminSalesListDrafts.TryGet(chatId, userId, out var reviewDraft) ||
                    !reviewDraft.IsReviewingExistingPerfume ||
                    reviewDraft.Stage != TelegramAdminSalesListStage.ReviewingExistingPerfume)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند بازبینی منقضی شده است.", cancellationToken);
                    return true;
                }
                if (parts[2] == "edit")
                {
                    reviewDraft.Stage = ExistingReviewStages[reviewDraft.ReviewFieldIndex];
                    _adminSalesListDrafts.Set(reviewDraft);
                    await _sender.AnswerCallbackAsync(callback.Id, "مقدار جدید را وارد کنید.", cancellationToken);
                    await ReplyAsync(chatId, ExistingEditPrompts[reviewDraft.ReviewFieldIndex], cancellationToken);
                    return true;
                }
                reviewDraft.ReviewFieldIndex++;
                _adminSalesListDrafts.Set(reviewDraft);
                await _sender.AnswerCallbackAsync(callback.Id, "تأیید شد ✅", cancellationToken);
                await SendExistingPerfumeReviewAsync(reviewDraft, cancellationToken);
                return true;

            case "owner" when parts.Length == 3 && parts[2] is "known" or "unknown":
                if (!_adminSalesListDrafts.TryGet(chatId, userId, out var ownerDraft) ||
                    ownerDraft.Stage != TelegramAdminSalesListStage.AwaitingBottleOwnerChoice)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", cancellationToken);
                    return true;
                }
                if (parts[2] == "unknown")
                {
                    ownerDraft.Stage = TelegramAdminSalesListStage.AwaitingNotes;
                    _adminSalesListDrafts.Set(ownerDraft);
                    await _sender.AnswerCallbackAsync(callback.Id, "فعلاً بدون صاحب باتل.", cancellationToken);
                    await ReplyAsync(chatId, "توضیحات لیست را وارد کنید؛ اگر ندارید خط تیره (-) بفرستید.", cancellationToken);
                }
                else
                {
                    ownerDraft.Stage = TelegramAdminSalesListStage.AwaitingBottleOwnerIdentity;
                    _adminSalesListDrafts.Set(ownerDraft);
                    await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
                    await ReplyAsync(chatId, "شناسه صاحب باتل را به‌صورت @username یا Telegram ID وارد کنید.", cancellationToken);
                }
                return true;

            case "cancel":
                _adminSalesListDrafts.Remove(chatId, userId);
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نویس لغو شد.", cancellationToken);
                await ReplyAsync(chatId, "ساخت لیست فروش لغو شد.", cancellationToken);
                return true;

            case "publish":
                await PublishSalesListDraftAsync(callback, cancellationToken);
                return true;

            default:
                await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", cancellationToken);
                return true;
        }
    }

    private async Task<bool> TryHandleAdminSalesListMessageAsync(
        TelegramMessage message,
        CancellationToken cancellationToken)
    {
        if (message.From is null ||
            !_adminSalesListDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, cancellationToken))
        {
            _adminSalesListDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }

        if (draft.Stage == TelegramAdminSalesListStage.AwaitingPhoto)
        {
            var photo = message.Photo?
                .OrderByDescending(value => (long)value.Width * value.Height)
                .FirstOrDefault();
            if (photo is null)
            {
                await ReplyAsync(message.Chat.Id, "لطفاً یک عکس از عطر ارسال کنید؛ فایل یا متن قابل قبول نیست.", cancellationToken);
                return true;
            }

            draft.PhotoFileId = photo.FileId;
            draft.Stage = TelegramAdminSalesListStage.Preview;
            _adminSalesListDrafts.Set(draft);
            await SendSalesListPreviewAsync(draft, cancellationToken);
            return true;
        }

        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return true;
        if (string.Equals(input, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _adminSalesListDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ساخت لیست فروش لغو شد.", cancellationToken);
            return true;
        }

        switch (draft.Stage)
        {
            case TelegramAdminSalesListStage.AwaitingPerfumeSearch:
                await SendPerfumeSearchResultsAsync(message.Chat.Id, input, cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingEnglishName:
                draft.EnglishName = Limit(input, 200);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingProductPageUrl;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "لینک کامل صفحه عطر در سایت عطردان را وارد کنید؛ باید با https:// شروع شود.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingProductPageUrl:
                if (!Uri.TryCreate(input, UriKind.Absolute, out var productUri) || productUri.Scheme != Uri.UriSchemeHttps)
                {
                    await ReplyAsync(message.Chat.Id, "لینک معتبر نیست. لینک کامل https صفحه عطر را وارد کنید.", cancellationToken);
                    return true;
                }
                draft.ProductPageUrl = Limit(productUri.ToString(), 500);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingBrand;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "نام برند را به انگلیسی وارد کنید؛ مثال: Chanel", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingBrand:
                draft.DisplayBrand = Limit(input, 150);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingGender;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "جنسیت عطر را وارد کنید: زنانه، مردانه یا یونیسکس", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingGender:
                if (!TryParseGender(input, out var gender))
                {
                    await ReplyAsync(message.Chat.Id, "فقط یکی از این سه مقدار را بفرستید: زنانه، مردانه، یونیسکس", cancellationToken);
                    return true;
                }
                draft.Gender = gender;
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingReleaseYear;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "سال تولید عطر را میلادی وارد کنید؛ مثال: 2021", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingReleaseYear:
                if (!TryParsePositiveInt(input, out var releaseYear) || releaseYear is < 1800 or > 2100)
                {
                    await ReplyAsync(message.Chat.Id, "سال تولید معتبر نیست؛ مثال: 2021", cancellationToken);
                    return true;
                }
                draft.ReleaseYear = releaseYear;
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingPersianName;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "نام فارسی عطر را وارد کنید.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingPersianName:
                draft.PersianName = Limit(input, 200);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingTopNotes;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, CombinedNotesPrompt, cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingTopNotes:
                if (!TryParseCombinedNotes(input, out var topNotes, out var middleNotes, out var baseNotes))
                {
                    await ReplyAsync(message.Chat.Id,
                        "فرمت نت‌ها معتبر نیست. یک خط برای تک‌نت یا دقیقاً سه خط برای نت ابتدایی، میانی و پایانی بفرستید.", cancellationToken);
                    return true;
                }
                draft.TopNotes = Limit(topNotes, 500);
                draft.MiddleNotes = Limit(middleNotes, 500);
                draft.BaseNotes = Limit(baseNotes, 500);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingAccords;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "آکوردهای اصلی را به فارسی وارد کنید.", cancellationToken);
                return true;

            // Keep active drafts created before a deployment usable.
            case TelegramAdminSalesListStage.AwaitingMiddleNotes:
                draft.MiddleNotes = Limit(input, 500);
                draft.Stage = TelegramAdminSalesListStage.AwaitingBaseNotes;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "نت‌های پایانی را به فارسی وارد کنید.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingBaseNotes:
                draft.BaseNotes = Limit(input, 500);
                draft.Stage = TelegramAdminSalesListStage.AwaitingAccords;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "آکوردهای اصلی را به فارسی وارد کنید.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingAccords:
                draft.Accords = Limit(input, 500);
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingPrice;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "قیمت فروش هر میل را به تومان وارد کنید؛ مثال: 150000", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingPrice:
                if (!TryParsePositiveDecimal(input, out var price))
                {
                    await ReplyAsync(message.Chat.Id, "قیمت معتبر نیست. قیمت هر میل را فقط به تومان وارد کنید؛ مثال: 150000", cancellationToken);
                    return true;
                }
                draft.PricePerMl = price;
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingVolume;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "برای باز کردن لیست جدید، حجم کل را به میل وارد کنید؛ مثال: 100", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingVolume:
                if (!TryParsePositiveInt(input, out var volume))
                {
                    await ReplyAsync(message.Chat.Id, "حجم هدف باید عددی مثبت باشد.", cancellationToken);
                    return true;
                }
                draft.TotalVolume = volume;
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingMinimumVolume;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "حداقل حجم قابل درخواست مشتری را وارد کنید؛ مثال: 1", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingMinimumVolume:
                if (!TryParsePositiveInt(input, out var minimumVolume) || minimumVolume > draft.TotalVolume)
                {
                    await ReplyAsync(message.Chat.Id, $"حداقل حجم باید عددی مثبت و حداکثر {draft.TotalVolume:N0} میل باشد.", cancellationToken);
                    return true;
                }
                draft.MinimumRequestVolumeMl = minimumVolume;
                if (await ContinueExistingPerfumeReviewAsync(draft, message.Chat.Id, cancellationToken)) return true;
                draft.Stage = TelegramAdminSalesListStage.AwaitingBottleOwnerChoice;
                _adminSalesListDrafts.Set(draft);
                await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                    "صاحب باتل این لیست مشخص است؟",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton("👑 ثبت صاحب باتل", "adminlist:owner:known") },
                        new[] { new TelegramInlineButton("فعلاً مشخص نیست", "adminlist:owner:unknown") }
                    }, cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingBottleOwnerIdentity:
                if (!(input.StartsWith('@') && input.Length > 1) && new string(input.Where(char.IsDigit).ToArray()).Length < 5)
                {
                    await ReplyAsync(message.Chat.Id, "شناسه نامعتبر است؛ @username یا Telegram ID وارد کنید.", cancellationToken);
                    return true;
                }
                draft.BottleOwnerIdentity = input;
                draft.Stage = TelegramAdminSalesListStage.AwaitingBottleOwnerVolume;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "صاحب باتل چند میل از باتل اصلی می‌خواهد؟", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingBottleOwnerVolume:
                if (!TryParsePositiveInt(input, out var ownerVolume) || ownerVolume > draft.TotalVolume)
                {
                    await ReplyAsync(message.Chat.Id, $"مقدار باید مثبت و حداکثر {draft.TotalVolume} میل باشد.", cancellationToken);
                    return true;
                }
                draft.BottleOwnerVolumeMl = ownerVolume;
                draft.Stage = TelegramAdminSalesListStage.AwaitingNotes;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "توضیحات لیست را وارد کنید؛ اگر ندارید خط تیره (-) بفرستید.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.AwaitingNotes:
                draft.Notes = input == "-" ? null : input.Length <= 500 ? input : input[..500];
                draft.Stage = TelegramAdminSalesListStage.AwaitingPhoto;
                _adminSalesListDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "حالا یک عکس واضح از عطر ارسال کنید. همین عکس همراه لیست در کانال اصلی منتشر می‌شود.", cancellationToken);
                return true;

            case TelegramAdminSalesListStage.Preview:
                await ReplyAsync(message.Chat.Id, "پیش‌نمایش آماده است؛ یکی از دکمه‌های انتشار، شروع دوباره یا لغو را بزنید.", cancellationToken);
                return true;

            default:
                return true;
        }
    }

    private async Task SendNewSalesListSourceMenuAsync(long chatId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SalesChannelId))
        {
            await ReplyAsync(chatId, "شناسه کانال اصلی هنوز در تنظیمات سرور ثبت نشده است.", cancellationToken);
            return;
        }

        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("➕ عطر جدید", "adminlist:newperfume") },
            new[] { new TelegramInlineButton("🔎 جستجوی عطرهای ثبت‌شده", "adminlist:search") },
            new[]
            {
                new TelegramInlineButton("لغو", "adminlist:cancel")
            }
        };
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), "برای ساخت لیست فروش جدید یکی از گزینه‌ها را انتخاب کنید:", buttons, cancellationToken);
    }

    private async Task SendPerfumeSearchResultsAsync(long chatId, string query, CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        var perfumes = (await _perfumeRepository.GetAllAsync(false, 500, cancellationToken))
            .Where(perfume => perfume.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                              perfume.EnglishName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                              perfume.Brand.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToArray();
        if (perfumes.Length == 0)
        {
            await ReplyAsync(chatId, "عطری پیدا نشد. عبارت دیگری بفرستید یا از منو «عطر جدید» را انتخاب کنید.", cancellationToken);
            return;
        }

        var buttons = perfumes.Select(perfume =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton($"{perfume.EnglishName} — {perfume.Brand}", $"adminlist:perfume:{perfume.Id:N}")
            }).Append(new[] { new TelegramInlineButton("❌ لغو", "adminlist:cancel") }).ToArray();
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), $"نتایج جستجو برای «{normalized}»:", buttons, cancellationToken);
    }

    private async Task StartNewPerfumeDraftAsync(TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        var draft = new TelegramAdminSalesListDraft
        {
            ChatId = callback.Message!.Chat.Id,
            UserId = callback.From.Id,
            IsNewPerfume = true,
            Stage = TelegramAdminSalesListStage.AwaitingEnglishName
        };
        _adminSalesListDrafts.Set(draft);
        await _sender.AnswerCallbackAsync(callback.Id, "ثبت عطر جدید آغاز شد.", cancellationToken);
        await ReplyAsync(draft.ChatId, "نام انگلیسی عطر جدید را وارد کنید.", cancellationToken);
    }

    private async Task StartSalesListDraftAsync(
        TelegramCallbackQuery callback,
        Guid perfumeId,
        CancellationToken cancellationToken)
    {
        var perfume = await _perfumeRepository.GetByIdAsync(perfumeId, cancellationToken);
        if (perfume is null || !perfume.IsActive)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این عطر دیگر قابل انتخاب نیست.", cancellationToken);
            return;
        }

        var previousList = await _salesListRepository.GetLatestByPerfumeIdAsync(
            perfume.Id,
            cancellationToken);

        var draft = new TelegramAdminSalesListDraft
        {
            ChatId = callback.Message!.Chat.Id,
            UserId = callback.From.Id,
            PerfumeId = perfume.Id,
            PerfumeName = perfume.Name,
            Brand = perfume.Brand,
            EnglishName = perfume.EnglishName,
            DisplayBrand = perfume.Brand,
            PersianName = perfume.Name,
            Stage = TelegramAdminSalesListStage.AwaitingEnglishName
        };

        if (previousList is not null)
        {
            draft.EnglishName = previousList.EnglishName;
            draft.ProductPageUrl = previousList.ProductPageUrl;
            draft.DisplayBrand = previousList.DisplayBrand;
            draft.Gender = (int)previousList.Gender;
            draft.ReleaseYear = previousList.ReleaseYear;
            draft.PersianName = previousList.PersianName;
            draft.TopNotes = previousList.TopNotes;
            draft.MiddleNotes = previousList.MiddleNotes;
            draft.BaseNotes = previousList.BaseNotes;
            draft.Accords = previousList.Accords;
            draft.PricePerMl = previousList.PricePerMl;
            draft.TotalVolume = previousList.TotalVolume;
            draft.MinimumRequestVolumeMl = previousList.MinimumRequestVolumeMl;
            draft.Notes = previousList.Notes;
            draft.IsReviewingExistingPerfume = true;
            draft.ReviewFieldIndex = 0;
            draft.Stage = TelegramAdminSalesListStage.ReviewingExistingPerfume;
        }

        _adminSalesListDrafts.Set(draft);
        await _sender.AnswerCallbackAsync(callback.Id, "عطر انتخاب شد.", cancellationToken);

        if (previousList is not null)
        {
            await ReplyAsync(draft.ChatId,
                $"عطر «{perfume.EnglishName} — {perfume.Brand}» قبلاً ثبت شده است. اطلاعات آخرین لیست را یکی‌یکی بررسی کنید.",
                cancellationToken);
            await SendExistingPerfumeReviewAsync(draft, cancellationToken);
            return;
        }

        await ReplyAsync(draft.ChatId, $"عطر «{perfume.EnglishName} — {perfume.Brand}» انتخاب شد.\nنام انگلیسی عطر را وارد کنید.", cancellationToken);
    }

    private async Task<bool> ContinueExistingPerfumeReviewAsync(
        TelegramAdminSalesListDraft draft,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (!draft.IsReviewingExistingPerfume)
            return false;

        draft.ReviewFieldIndex++;
        draft.Stage = TelegramAdminSalesListStage.ReviewingExistingPerfume;
        _adminSalesListDrafts.Set(draft);
        await ReplyAsync(chatId, "اصلاح شد ✅", cancellationToken);
        await SendExistingPerfumeReviewAsync(draft, cancellationToken);
        return true;
    }

    private async Task SendExistingPerfumeReviewAsync(
        TelegramAdminSalesListDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.ReviewFieldIndex >= ExistingReviewStages.Length)
        {
            draft.IsReviewingExistingPerfume = false;
            draft.Stage = TelegramAdminSalesListStage.AwaitingBottleOwnerChoice;
            _adminSalesListDrafts.Set(draft);
            await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
                "اطلاعات قبلی بررسی شد ✅\nصاحب باتل این لیست مشخص است؟",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[] { new TelegramInlineButton("👑 ثبت صاحب باتل", "adminlist:owner:known") },
                    new[] { new TelegramInlineButton("فعلاً مشخص نیست", "adminlist:owner:unknown") }
                }, cancellationToken);
            return;
        }

        draft.Stage = TelegramAdminSalesListStage.ReviewingExistingPerfume;
        _adminSalesListDrafts.Set(draft);
        var index = draft.ReviewFieldIndex;
        var text = $"بررسی اطلاعات قبلی — {index + 1} از {ExistingReviewStages.Length}\n\n" +
                   $"{ExistingReviewLabels[index]}:\n{ExistingReviewValue(draft, index)}\n\n" +
                   "این مقدار صحیح است؟";
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(), text,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ تأیید", "adminlist:review:keep"),
                    new TelegramInlineButton("✏️ اصلاح", "adminlist:review:edit")
                },
                new[] { new TelegramInlineButton("❌ لغو", "adminlist:cancel") }
            }, cancellationToken);
    }

    private static string ExistingReviewValue(TelegramAdminSalesListDraft draft, int index) => index switch
    {
        0 => draft.EnglishName,
        1 => draft.ProductPageUrl,
        2 => draft.DisplayBrand,
        3 => GenderLabel(draft.Gender),
        4 => draft.ReleaseYear.ToString(CultureInfo.InvariantCulture),
        5 => draft.PersianName,
        6 => FormatNotesForReview(draft.TopNotes, draft.MiddleNotes, draft.BaseNotes),
        7 => draft.Accords,
        8 => $"{draft.PricePerMl:N0} تومان",
        9 => $"{draft.TotalVolume:N0} میل",
        10 => $"{draft.MinimumRequestVolumeMl:N0} میل",
        _ => "—"
    };

    private async Task SendSalesListPreviewAsync(
        TelegramAdminSalesListDraft draft,
        CancellationToken cancellationToken)
    {
        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("✅ انتشار در کانال اصلی", "adminlist:publish") },
            new[]
            {
                new TelegramInlineButton("🔄 شروع دوباره", "adminlist:restart"),
                new TelegramInlineButton("❌ لغو", "adminlist:cancel")
            }
        };
        var caption = "پیش‌نمایش لیست فروش:\n\n" + FormatSalesListAnnouncement(draft);
        if (!string.IsNullOrWhiteSpace(draft.PhotoFileId))
        {
            var preview = await _sender.SendPhotoAsync(
                draft.ChatId.ToString(), draft.PhotoFileId, caption, cancellationToken);
            if (!preview.IsSuccessful)
            {
                await ReplyAsync(draft.ChatId, $"نمایش عکس ناموفق بود: {preview.Error}", cancellationToken);
                return;
            }
        }
        await _sender.SendInlineKeyboardAsync(
            draft.ChatId.ToString(),
            $"کانال مقصد: {_options.SalesChannelId}\nپس از بررسی عکس و متن، انتشار را تأیید کنید.",
            buttons,
            cancellationToken);
    }

    private async Task PublishSalesListDraftAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!_adminSalesListDrafts.TryGet(chatId, callback.From.Id, out var draft) ||
            draft.Stage != TelegramAdminSalesListStage.Preview)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نویس معتبر پیدا نشد.", cancellationToken);
            return;
        }

        try
        {
            if (!draft.SalesListId.HasValue)
            {
                if (!draft.PerfumeId.HasValue)
                {
                    var perfume = await _mediator.Send(new CreatePerfumeCommand(
                        draft.PersianName,
                        draft.EnglishName,
                        draft.DisplayBrand,
                        draft.PricePerMl,
                        draft.TotalVolume,
                        draft.Notes), cancellationToken);
                    draft.PerfumeId = perfume.Id;
                    draft.PerfumeName = perfume.Name;
                    draft.Brand = perfume.Brand;
                }

                var created = await _mediator.Send(
                    new CreateSalesListCommand(
                        draft.PerfumeId.Value,
                        draft.PricePerMl,
                        draft.TotalVolume,
                        _options.SalesChannelId,
                        draft.Notes,
                        draft.MinimumRequestVolumeMl,
                        draft.EnglishName,
                        draft.ProductPageUrl,
                        draft.DisplayBrand,
                        draft.Gender,
                        draft.ReleaseYear,
                        draft.PersianName,
                        draft.TopNotes,
                        draft.MiddleNotes,
                        draft.BaseNotes,
                        draft.Accords),
                    cancellationToken);
                draft.SalesListId = created.Id;
                if (!string.IsNullOrWhiteSpace(draft.BottleOwnerIdentity) && draft.BottleOwnerVolumeMl > 0)
                {
                    var ownerIdentity = draft.BottleOwnerIdentity.Trim();
                    var ownerUsername = ownerIdentity.StartsWith('@') ? ownerIdentity.TrimStart('@') : null;
                    var ownerTelegramId = ownerUsername is null
                        ? new string(ownerIdentity.Where(char.IsDigit).ToArray())
                        : $"admin-username:{ownerUsername.ToLowerInvariant()}";
                    var ownerRequest = new ZibasheERP.Domain.Entities.SalesListRequest
                    {
                        Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, SalesListId = created.Id,
                        TelegramUserId = ownerTelegramId, TelegramUsername = ownerUsername,
                        VolumeMl = draft.BottleOwnerVolumeMl, PerfumePricePerMl = draft.PricePerMl,
                        BottlePrice = 0, IsBottleOwner = true,
                        Kind = ZibasheERP.Domain.Entities.SalesListRequestKind.CurrentBottle,
                        Status = ZibasheERP.Domain.Entities.SalesListRequestStatus.PendingConfirmation,
                        CreatedByAdmin = true, ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                        ExternalReference = $"admin-list-owner:{Guid.NewGuid():N}"
                    };
                    await _salesListRequestRepository.AddAsync(ownerRequest, cancellationToken);
                    await _salesListRequestRepository.SaveChangesAsync(cancellationToken);
                    await _salesListRequestRepository.ConfirmCurrentBottleAsync(
                        ownerRequest.Id, ownerTelegramId, cancellationToken);
                }
                _adminSalesListDrafts.Set(draft);
            }

            if (string.IsNullOrWhiteSpace(draft.PhotoFileId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "عکس پیش‌نویس پیدا نشد.", cancellationToken);
                return;
            }
            var salesList = await _salesListRepository.GetByIdAsync(draft.SalesListId.Value, cancellationToken);
            if (salesList is null)
                throw new InvalidOperationException("لیست فروش ساخته شد اما قابل بازیابی نیست.");
            var initialRequests = await _salesListRequestRepository.GetConfirmedAsync(salesList.Id, cancellationToken);
            var result = await _sender.SendPhotoWithKeyboardAsync(
                _options.SalesChannelId,
                draft.PhotoFileId,
                FormatChannelSalesList(salesList, initialRequests),
                BuildChannelVolumeButtons(salesList),
                cancellationToken);
            if (!result.IsSuccessful)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "انتشار ناموفق بود؛ دوباره تلاش کنید.", cancellationToken);
                await ReplyAsync(chatId, $"ارسال به کانال اصلی ناموفق بود: {result.Error}", cancellationToken);
                return;
            }

            salesList.TelegramChannelId = _options.SalesChannelId;
            salesList.TelegramMessageId = result.MessageId;
            salesList.TelegramPhotoFileId = draft.PhotoFileId;
            salesList.UpdatedAt = DateTime.UtcNow;
            await _salesListRepository.UpdateAsync(salesList, cancellationToken);
            await _salesListRepository.SaveChangesAsync(cancellationToken);

            var discussionText =
                    $"💬 هر سؤالی در رابطه با عطر «{draft.EnglishName}» دارید، اینجا بپرسید.\n" +
                    "اگر مقدار موردنظر شما در دکمه‌ها نیست، آن را در کامنت بنویسید تا ادمین ثبت کند.";
            var discussion = await _sender.SendReplyAsync(
                _options.SalesChannelId, discussionText, result.MessageId!.Value, cancellationToken);
            if (discussion.IsSuccessful)
                salesList.TelegramDiscussionMessageId = discussion.MessageId;
            await _salesListRepository.SaveChangesAsync(cancellationToken);
            await SendRemainingVolumeAlertsAsync(salesList, cancellationToken);

            _adminSalesListDrafts.Remove(chatId, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "منتشر شد ✅", cancellationToken);
            await ReplyAsync(chatId, "لیست فروش با موفقیت در کانال اصلی منتشر شد ✅", cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FluentValidation.ValidationException)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ساخت لیست ناموفق بود.", cancellationToken);
            await ReplyAsync(chatId, exception.Message, cancellationToken);
        }
    }

    private static string FormatSalesListAnnouncement(TelegramAdminSalesListDraft draft) =>
        $"🌿 لیست فروش جدید زیباشی\n" +
        $"🧴 {draft.EnglishName}\n" +
        $"🔗 {draft.ProductPageUrl}\n" +
        $"🏷 #{NormalizeBrandTag(draft.DisplayBrand)} — {GenderLabel(draft.Gender)} — L.{draft.ReleaseYear}\n" +
        $"🇮🇷 {draft.PersianName}\n" +
        FormatNotesForAnnouncement(draft.TopNotes, draft.MiddleNotes, draft.BaseNotes) +
        $"🎼 آکوردها: {draft.Accords}\n" +
        $"💧 حجم کل: {draft.TotalVolume:N0} میل\n" +
        $"📏 حداقل درخواست: {draft.MinimumRequestVolumeMl:N0} میل\n" +
        (string.IsNullOrWhiteSpace(draft.BottleOwnerIdentity)
            ? "👑 صاحب باتل: فعلاً مشخص نیست\n"
            : $"👑 صاحب باتل: {draft.BottleOwnerIdentity} — {draft.BottleOwnerVolumeMl} میل\n") +
        $"💰 قیمت هر میل: {draft.PricePerMl:N0} تومان" +
        (string.IsNullOrWhiteSpace(draft.Notes) ? string.Empty : $"\n📝 {draft.Notes}");

    private static bool TryParsePositiveDecimal(string value, out decimal result) =>
        decimal.TryParse(NormalizeNumber(value), NumberStyles.Number, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryParsePositiveInt(string value, out int result) =>
        int.TryParse(NormalizeNumber(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;

    private static string NormalizeNumber(string value)
    {
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        const string arabic = "٠١٢٣٤٥٦٧٨٩";
        var chars = value.Trim().Where(character => character is not ',' and not '٬' and not ' ').Select(character =>
        {
            var index = persian.IndexOf(character);
            if (index >= 0) return (char)('0' + index);
            index = arabic.IndexOf(character);
            return index >= 0 ? (char)('0' + index) : character;
        });
        return new string(chars.ToArray());
    }

    private static string Limit(string value, int maximum) =>
        value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];

    private static bool TryParseCombinedNotes(
        string value, out string topNotes, out string middleNotes, out string baseNotes)
    {
        var parts = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('|', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        topNotes = middleNotes = baseNotes = string.Empty;
        if (parts.Length == 1)
        {
            topNotes = StripNoteLabel(parts[0]);
            return topNotes.Length > 0;
        }
        if (parts.Length != 3) return false;
        topNotes = StripNoteLabel(parts[0]);
        middleNotes = StripNoteLabel(parts[1]);
        baseNotes = StripNoteLabel(parts[2]);
        return topNotes.Length > 0 && middleNotes.Length > 0 && baseNotes.Length > 0;
    }

    private static string StripNoteLabel(string value)
    {
        var separator = value.IndexOf(':');
        if (separator < 0) separator = value.IndexOf('：');
        return separator >= 0 ? value[(separator + 1)..].Trim() : value.Trim();
    }

    private static string FormatNotesForReview(string topNotes, string middleNotes, string baseNotes)
    {
        var notes = new[] { topNotes, middleNotes, baseNotes }
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return notes.Length <= 1
            ? notes.FirstOrDefault() ?? "—"
            : $"ابتدایی: {topNotes}\nمیانی: {middleNotes}\nپایانی: {baseNotes}";
    }

    private static string FormatNotesForAnnouncement(string topNotes, string middleNotes, string baseNotes)
    {
        var notes = new[] { topNotes, middleNotes, baseNotes }
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (notes.Length == 0) return string.Empty;
        if (notes.Length == 1) return $"🌸 نت: {notes[0]}\n";
        return $"🍊 نت‌های ابتدایی: {topNotes}\n" +
               $"🌸 نت‌های میانی: {middleNotes}\n" +
               $"🌳 نت‌های پایانی: {baseNotes}\n";
    }

    private static bool TryParseGender(string value, out int gender)
    {
        var normalized = value.Trim().ToLowerInvariant();
        gender = normalized switch
        {
            "زنانه" or "women" or "female" => 1,
            "مردانه" or "men" or "male" => 2,
            "یونیسکس" or "unisex" => 3,
            _ => 0
        };
        return gender != 0;
    }

    private static string GenderLabel(int gender) => gender switch
    {
        1 => "#women 👩",
        2 => "#men 👨",
        _ => "#unisex 👩‍🦰👨"
    };

    private static string NormalizeBrandTag(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value.Trim(), @"[^\p{L}\p{N}]+", "_").Trim('_');
}
