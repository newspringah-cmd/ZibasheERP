using ZibasheERP.API.Telegram;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using System.Text.Json;
using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleAdminCommandAsync(TelegramMessage message, CancellationToken ct)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) ||
            !(text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/bank", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/nextbottle", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/listrequest", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/bottleprice", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/perfumepercent", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("/whoami", StringComparison.OrdinalIgnoreCase)))
            return false;

        if ((text.Equals("/whoami", StringComparison.OrdinalIgnoreCase) ||
             text.StartsWith("/whoami@", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(_options.AdminChatId, out var adminChatId) && adminChatId == message.Chat.Id)
        {
            await ReplyAsync(message.Chat.Id, $"Telegram User ID دریافت‌شده توسط ربات: {message.From!.Id}", ct);
            return true;
        }

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From!.Id, ct))
        {
            await ReplyAsync(message.Chat.Id, "این بخش فقط برای مدیران گروه حسابداری فعال است.", ct);
            return true;
        }

        if (text.StartsWith("/bottleprice ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/perfumepercent ", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPrimaryOwner(message.From.Id))
            {
                await ReplyAsync(message.Chat.Id, "تغییر قیمت فقط برای مدیر اصلی سیستم مجاز است.", ct);
                return true;
            }
            if (text.StartsWith("/bottleprice ", StringComparison.OrdinalIgnoreCase))
            {
                var values = text[13..].Split('|', StringSplitOptions.TrimEntries);
                var type = values.ElementAtOrDefault(0)?.ToLowerInvariant() switch
                {
                    "نرمال" or "normal" => BottleType.Normal,
                    "فانتزی" or "fancy" => BottleType.Fancy,
                    _ => (BottleType?)null
                };
                if (values.Length != 4 || type is null ||
                    !TryParsePositiveInt(values[1], out var minimum) ||
                    !TryParsePositiveInt(values[2], out var maximum) || minimum > maximum ||
                    !TryParsePositiveDecimal(values[3], out var price))
                {
                    await ReplyAsync(message.Chat.Id,
                        "فرمت صحیح:\n/bottleprice نرمال یا فانتزی | حداقل میل | حداکثر میل | قیمت تومان", ct);
                    return true;
                }
                var affected = PriceableVolumes(type.Value, minimum, maximum);
                if (affected.Length == 0)
                {
                    await ReplyAsync(message.Chat.Id, "در این بازه حجم استاندارد و مجازی برای این نوع شیشه وجود ندارد.", ct);
                    return true;
                }
                _ownerPricingDrafts.Set(new TelegramOwnerPricingDraft
                {
                    ChatId = message.Chat.Id, UserId = message.From.Id,
                    Kind = TelegramOwnerPricingKind.BottleRange, BottleType = type,
                    MinimumVolumeMl = minimum, MaximumVolumeMl = maximum, Value = price,
                    Stage = TelegramOwnerPricingStage.AwaitingConfirmation
                });
                await SendOwnerPriceConfirmationAsync(message.Chat.Id,
                    $"نوع: {(type == BottleType.Normal ? "نرمال" : "فانتزی")}\n" +
                    $"حجم‌های تحت تأثیر: {string.Join("، ", affected)} میل\nقیمت جدید هر شیشه: {price:N0} تومان", ct);
                return true;
            }

            var percentText = text[16..].Trim();
            if (!decimal.TryParse(NormalizeNumber(percentText), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent is <= -100 or > 1000 || percent == 0)
            {
                await ReplyAsync(message.Chat.Id,
                    "درصد را با علامت وارد کنید؛ مثال افزایش ۵ درصد: /perfumepercent +5\nکاهش ۵ درصد: /perfumepercent -5", ct);
                return true;
            }
            var perfumes = await _perfumeRepository.GetAllAsync(false, 200, ct);
            _ownerPricingDrafts.Set(new TelegramOwnerPricingDraft
            {
                ChatId = message.Chat.Id, UserId = message.From.Id,
                Kind = TelegramOwnerPricingKind.PerfumePercentage, Value = percent,
                Stage = TelegramOwnerPricingStage.AwaitingConfirmation
            });
            var samples = perfumes.Take(3).Select(value =>
                $"{value.EnglishName}: {value.PricePerMl:N0} ← {AdjustedPrice(value.PricePerMl, percent):N0}");
            var openListsCount = (await _salesListRepository.GetForAdminAsync(200, ct))
                .Count(value => value.Status == SalesListStatus.Open);
            await SendOwnerPriceConfirmationAsync(message.Chat.Id,
                $"تغییر قیمت کاتالوگ {perfumes.Count} عطر: {percent:+0.##;-0.##}%\n" +
                string.Join("\n", samples) +
                $"\n{openListsCount} لیست فروش باز نیز به‌روزرسانی می‌شود؛ فاکتورها و لیست‌های بسته تغییر نمی‌کنند.", ct);
            return true;
        }

        if (text.StartsWith("/bankadd ", StringComparison.OrdinalIgnoreCase))
        {
            var values = text[9..].Split('|', StringSplitOptions.TrimEntries);
            if (values.Length != 3 || !TryNormalizeCard(values[0], out var card))
            {
                await ReplyAsync(message.Chat.Id, "فرمت صحیح:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک", ct);
                return true;
            }
            var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
            if (accounts.Count >= 4)
            {
                await ReplyAsync(message.Chat.Id, "حداکثر ۴ حساب بانکی قابل ثبت است. ابتدا یکی از حساب‌های قبلی را حذف کنید.", ct);
                return true;
            }
            await _paymentAccountRepository.AddAsync(new InvoicePaymentAccount
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CardNumber = card,
                AccountHolder = values[1], BankName = values[2],
                DisplayOrder = accounts.Count, IsActive = true
            }, ct);
            await _paymentAccountRepository.SaveChangesAsync(ct);
            await SendInvoiceAdminMenuAsync(message.Chat.Id, "حساب بانکی اضافه شد ✅", ct);
            return true;
        }

        if (text.StartsWith("/nextbottle ", StringComparison.OrdinalIgnoreCase))
        {
            var values = text[12..].Split('|', StringSplitOptions.TrimEntries);
            if (values.Length != 3 || !TryParsePositiveInt(values[2], out var volume))
            {
                await ReplyAsync(message.Chat.Id,
                    "فرمت صحیح:\n/nextbottle کدلیست | @username یا TelegramId | مقدارمیل", ct);
                return true;
            }
            var code = values[0].Trim();
            var lists = await _salesListRepository.GetForAdminAsync(200, ct);
            var matches = lists.Where(value =>
                value.PublicCode.ToString() == code ||
                value.Id.ToString("N").StartsWith(code, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                await ReplyAsync(message.Chat.Id,
                    matches.Length == 0 ? "لیستی با این کد پیدا نشد." : "کد واردشده یکتا نیست؛ تعداد بیشتری از حروف کد را وارد کنید.", ct);
                return true;
            }
            var identity = values[1].Trim();
            var username = identity.StartsWith('@') ? identity.TrimStart('@') : null;
            var telegramId = username is null ? identity : $"admin-username:{username.ToLowerInvariant()}";
            var request = new SalesListRequest
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                SalesListId = matches[0].Id,
                TelegramUserId = telegramId,
                TelegramUsername = username,
                VolumeMl = volume,
                PerfumePricePerMl = matches[0].PricePerMl,
                Kind = SalesListRequestKind.NextBottle,
                Status = SalesListRequestStatus.Confirmed,
                CreatedByAdmin = true,
                ExpiresAt = DateTime.MaxValue,
                ConfirmedAt = DateTime.UtcNow,
                ExternalReference = $"admin-next-bottle:{Guid.NewGuid():N}"
            };
            await _salesListRequestRepository.AddAsync(request, ct);
            await _salesListRequestRepository.SaveChangesAsync(ct);
            await RefreshChannelSalesListAsync(matches[0].Id, ct);
            await ReplyAsync(message.Chat.Id,
                $"{volume} میل برای {(username is null ? identity : "@" + username)} در صف Next Bottle ثبت شد ✅", ct);
            return true;
        }

        if (text.StartsWith("/listrequest ", StringComparison.OrdinalIgnoreCase))
        {
            var values = text[13..].Split('|', StringSplitOptions.TrimEntries);
            if (values.Length != 4 || !TryParsePositiveInt(values[2], out var volume))
            {
                await ReplyAsync(message.Chat.Id,
                    "فرمت صحیح:\n/listrequest کدلیست | @username یا TelegramId | مقدارمیل | نرمال یا فانتزی", ct);
                return true;
            }
            var lists = await _salesListRepository.GetForAdminAsync(200, ct);
            var matches = lists.Where(value =>
                value.PublicCode.ToString() == values[0].Trim() ||
                value.Id.ToString("N").StartsWith(values[0].Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                await ReplyAsync(message.Chat.Id, matches.Length == 0
                    ? "لیستی با این کد پیدا نشد."
                    : "کد واردشده یکتا نیست؛ تعداد بیشتری از حروف کد را وارد کنید.", ct);
                return true;
            }
            var requestedType = values[3].Contains("فانتزی", StringComparison.OrdinalIgnoreCase)
                ? BottleType.Fancy : BottleType.Normal;
            var bottles = await _mediator.Send(
                new ZibasheERP.Application.Features.Bottles.GetAvailableBottles.GetAvailableBottlesQuery(volume), ct);
            var bottle = bottles.FirstOrDefault(value => string.Equals(
                value.Type, requestedType.ToString(), StringComparison.OrdinalIgnoreCase));
            if (bottle is null)
            {
                await ReplyAsync(message.Chat.Id, "برای این حجم، شیشه فعال از نوع انتخاب‌شده وجود ندارد.", ct);
                return true;
            }
            var identity = values[1].Trim();
            var username = identity.StartsWith('@') ? identity.TrimStart('@') : null;
            var telegramId = username is null ? identity : $"admin-username:{username.ToLowerInvariant()}";
            var request = new SalesListRequest
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                SalesListId = matches[0].Id,
                TelegramUserId = telegramId,
                TelegramUsername = username,
                VolumeMl = volume,
                BottleId = bottle.Id,
                PerfumePricePerMl = matches[0].PricePerMl,
                BottlePrice = bottle.Price,
                Kind = SalesListRequestKind.CurrentBottle,
                Status = SalesListRequestStatus.PendingConfirmation,
                CreatedByAdmin = true,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                ExternalReference = $"admin-custom-request:{Guid.NewGuid():N}"
            };
            await _salesListRequestRepository.AddAsync(request, ct);
            await _salesListRequestRepository.SaveChangesAsync(ct);
            await _salesListRequestRepository.ConfirmCurrentBottleAsync(request.Id, telegramId, ct);
            await RefreshChannelSalesListAsync(matches[0].Id, ct);
            await ReplyAsync(message.Chat.Id,
                $"درخواست دستی {volume} میل با شیشه {values[3]} ثبت شد ✅", ct);
            return true;
        }

        await SendInvoiceAdminMenuAsync(message.Chat.Id, null, ct);
        return true;
    }

    private async Task<bool> TryHandleAdminCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            callback.Data.StartsWith("import:", StringComparison.Ordinal) == false &&
            !(callback.Data.StartsWith("invoiceadmin:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("invoicebatch:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("invoicepay:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("invoiceinventory:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("ownerprice:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("adminrequest:", StringComparison.Ordinal)))
            return false;
        if (callback.Data.StartsWith("import:", StringComparison.Ordinal))
        {
            await HandleSalesListImportCallbackAsync(callback, ct);
            return true;
        }
        if (callback.Data.StartsWith("invoicepay:", StringComparison.Ordinal))
        {
            await HandleInvoicePaymentStatusCallbackAsync(callback, ct);
            return true;
        }
        if (callback.Data.StartsWith("invoiceinventory:", StringComparison.Ordinal))
        {
            await HandleInvoiceInventoryCallbackAsync(callback, ct);
            return true;
        }
        if (!await IsAuthorizedInvoiceAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت ندارید.", ct);
            return true;
        }

        if (callback.Data.StartsWith("ownerprice:", StringComparison.Ordinal))
        {
            if (!IsPrimaryOwner(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فقط مدیر اصلی مجاز است.", ct);
                return true;
            }
            if (callback.Data == "ownerprice:cancel")
            {
                _ownerPricingDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
                await _sender.AnswerCallbackAsync(callback.Id, "لغو شد.", ct);
                return true;
            }
            if (callback.Data == "ownerprice:bottle")
            {
                _ownerPricingDrafts.Set(new TelegramOwnerPricingDraft
                {
                    ChatId = callback.Message.Chat.Id, UserId = callback.From.Id,
                    Kind = TelegramOwnerPricingKind.BottleRange,
                    Stage = TelegramOwnerPricingStage.AwaitingBottleType
                });
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                    "نوع شیشه را انتخاب کنید:",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[]
                        {
                            new TelegramInlineButton("شیشه نرمال", "ownerprice:type:normal"),
                            new TelegramInlineButton("شیشه فانتزی", "ownerprice:type:fancy")
                        },
                        new[] { new TelegramInlineButton("❌ لغو", "ownerprice:cancel") }
                    }, ct);
                return true;
            }
            if (callback.Data.StartsWith("ownerprice:type:", StringComparison.Ordinal))
            {
                if (!_ownerPricingDrafts.TryGet(callback.Message.Chat.Id, callback.From.Id, out var draft) ||
                    draft.Kind != TelegramOwnerPricingKind.BottleRange)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                    return true;
                }
                draft.BottleType = callback.Data.EndsWith(":normal", StringComparison.Ordinal)
                    ? BottleType.Normal : BottleType.Fancy;
                draft.Stage = TelegramOwnerPricingStage.AwaitingMinimumVolume;
                _ownerPricingDrafts.Set(draft);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callback.Message.Chat.Id,
                    "حداقل حجم شیشه را به میل وارد کنید؛ مثال: 5", ct);
                return true;
            }
            if (callback.Data == "ownerprice:perfume")
            {
                _ownerPricingDrafts.Set(new TelegramOwnerPricingDraft
                {
                    ChatId = callback.Message.Chat.Id, UserId = callback.From.Id,
                    Kind = TelegramOwnerPricingKind.PerfumePercentage,
                    Stage = TelegramOwnerPricingStage.AwaitingPercentageDirection
                });
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                    "نوع تغییر قیمت همه عطرهای فعال را انتخاب کنید:",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[]
                        {
                            new TelegramInlineButton("📈 افزایش", "ownerprice:direction:up"),
                            new TelegramInlineButton("📉 کاهش", "ownerprice:direction:down")
                        },
                        new[] { new TelegramInlineButton("❌ لغو", "ownerprice:cancel") }
                    }, ct);
                return true;
            }
            if (callback.Data.StartsWith("ownerprice:direction:", StringComparison.Ordinal))
            {
                if (!_ownerPricingDrafts.TryGet(callback.Message.Chat.Id, callback.From.Id, out var draft) ||
                    draft.Kind != TelegramOwnerPricingKind.PerfumePercentage)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                    return true;
                }
                draft.PercentageSign = callback.Data.EndsWith(":down", StringComparison.Ordinal) ? -1 : 1;
                draft.Stage = TelegramOwnerPricingStage.AwaitingPercentageValue;
                _ownerPricingDrafts.Set(draft);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callback.Message.Chat.Id,
                    $"درصد {(draft.PercentageSign > 0 ? "افزایش" : "کاهش")} را بدون علامت وارد کنید؛ مثال: 5", ct);
                return true;
            }
            await ApplyOwnerPricingDraftAsync(callback, ct);
            return true;
        }
        if (callback.Data.StartsWith("adminrequest:", StringComparison.Ordinal))
        {
            await HandleAdminRequestCallbackAsync(callback, ct);
            return true;
        }
        if (callback.Data.StartsWith("invoicebatch:", StringComparison.Ordinal))
        {
            await HandleInvoiceBatchCallbackAsync(callback, ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:batch")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendInvoiceBatchSelectionAsync(callback.Message.Chat.Id, callback.From.Id, ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:waiting")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendWaitingInvoiceListsAsync(callback.Message.Chat.Id, ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:manual")
        {
            _manualInvoiceDrafts.Set(new TelegramManualInvoiceDraft
            {
                ChatId = callback.Message.Chat.Id, UserId = callback.From.Id
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "🧾 صدور فاکتور دستی\n\nشناسه مشتری را به صورت @username یا Telegram ID وارد کنید:", ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:pricing")
        {
            if (!IsPrimaryOwner(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این بخش فقط برای مدیر اصلی است.", ct);
                return true;
            }
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                "کدام قیمت را می‌خواهید مدیریت کنید؟",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[] { new TelegramInlineButton("🧴 قیمت شیشه‌ها", "ownerprice:bottle") },
                    new[] { new TelegramInlineButton("🌸 درصد قیمت عطرها", "ownerprice:perfume") },
                    new[] { new TelegramInlineButton("❌ لغو", "ownerprice:cancel") }
                }, ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:sticker")
        {
            if (!IsPrimaryOwner(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این بخش فقط برای مدیر اصلی است.", ct);
                return true;
            }
            _invoiceStickerDrafts.Start(callback.Message.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "👋 استیکر سلام جدید را همین‌جا ارسال کنید. این ورودی تا ۵ دقیقه فعال است.", ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:sticker-clear")
        {
            if (!IsPrimaryOwner(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این بخش فقط برای مدیر اصلی است.", ct);
                return true;
            }
            await _invoiceTelegramSettingRepository.SetGreetingStickerFileIdAsync(null, callback.From.Id, ct);
            await _sender.AnswerCallbackAsync(callback.Id, "استیکر حذف شد.", ct);
            await SendInvoiceAdminMenuAsync(callback.Message.Chat.Id,
                "از این پس پیام «سلام 👋» ارسال می‌شود.", ct);
            return true;
        }

        var parts = callback.Data.Split(':');
        if (parts.Length == 3 && Guid.TryParseExact(parts[2], "N", out var id))
        {
            var account = await _paymentAccountRepository.GetByIdAsync(id, ct);
            if (account is not null)
            {
                if (parts[1] == "toggle") account.IsActive = !account.IsActive;
                if (parts[1] == "delete") account.IsDeleted = true;
                account.UpdatedAt = DateTime.UtcNow;
                await _paymentAccountRepository.SaveChangesAsync(ct);
            }
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        await SendInvoiceAdminMenuAsync(callback.Message.Chat.Id, null, ct);
        return true;
    }

    private async Task HandleSalesListImportCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var callbackMessage = callback.Message!;
        var parts = callback.Data!.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "N", out var importId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "شناسه واردات نامعتبر است.", ct);
            return;
        }
        var item = await _db.TelegramSalesListImports.FirstOrDefaultAsync(value =>
            value.Id == importId && !value.IsDeleted, ct);
        if (item is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "رکورد واردات پیدا نشد.", ct);
            return;
        }
        var reviewChatId = item.ReviewChatId?.Trim();
        var canReview = IsPrimaryOwner(callback.From.Id) ||
            (!string.IsNullOrWhiteSpace(reviewChatId) &&
             reviewChatId == callbackMessage.Chat.Id.ToString() &&
             await _sender.IsChatAdministratorAsync(reviewChatId, callback.From.Id.ToString(), ct));
        if (!canReview)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مدیریت این گروه را ندارید.", ct);
            return;
        }
        if (item.Status is TelegramSalesListImportStatus.Rejected or TelegramSalesListImportStatus.Imported)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این مورد قبلاً نهایی شده است.", ct);
            return;
        }
        switch (parts[1])
        {
            case "reject":
                item.Status = TelegramSalesListImportStatus.Rejected;
                item.ReviewedAt = DateTime.UtcNow;
                item.ReviewedByTelegramUserId = callback.From.Id.ToString();
                await _db.SaveChangesAsync(ct);
                await _sender.EditCaptionWithKeyboardAsync(callbackMessage.Chat.Id.ToString(), callbackMessage.MessageId,
                    "❌ این لیست رد شد.", Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
                await _sender.AnswerCallbackAsync(callback.Id, "رد شد.", ct);
                break;
            case "edit":
                item.Status = TelegramSalesListImportStatus.NeedsEditing;
                item.ReviewedAt = DateTime.UtcNow;
                item.ReviewedByTelegramUserId = callback.From.Id.ToString();
                await _db.SaveChangesAsync(ct);
                await _sender.AnswerCallbackAsync(callback.Id, "برای ویرایش، متن اصلاح‌شده را در گروه تست ارسال کنید.", ct);
                break;
            case "approve":
                if (item.Status != TelegramSalesListImportStatus.PendingReview &&
                    item.Status != TelegramSalesListImportStatus.NeedsEditing)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "وضعیت این مورد قابل تأیید نیست.", ct);
                    return;
                }
                await ImportApprovedSalesListAsync(item, callback, ct);
                break;
            default:
                await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct);
                break;
        }
    }

    private async Task ImportApprovedSalesListAsync(
        TelegramSalesListImport item, TelegramCallbackQuery callback, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            using var json = JsonDocument.Parse(item.ParsedPayload);
            var value = json.RootElement;
            var english = value.GetProperty("englishName").GetString() ?? throw new InvalidOperationException("نام انگلیسی ناقص است.");
            var brand = value.GetProperty("displayBrand").GetString() ?? "Unknown";
            var persian = value.GetProperty("persianName").GetString() ?? english;
            var topNotes = value.TryGetProperty("topNotes", out var top) ? top.GetString() ?? string.Empty : string.Empty;
            var middleNotes = value.TryGetProperty("middleNotes", out var middle) ? middle.GetString() ?? string.Empty : string.Empty;
            var baseNotes = value.TryGetProperty("baseNotes", out var bottom) ? bottom.GetString() ?? string.Empty : string.Empty;
            var accords = value.TryGetProperty("accords", out var accordValue) ? accordValue.GetString() ?? string.Empty : string.Empty;
            var productUrl = value.TryGetProperty("productPageUrl", out var urlValue) ? urlValue.GetString() : null;
            var price = value.GetProperty("pricePerMl").GetDecimal();
            var total = value.GetProperty("totalVolumeMl").GetInt32();
            var minimum = value.TryGetProperty("minimumRequestVolumeMl", out var min) && min.ValueKind != JsonValueKind.Null ? min.GetInt32() : 1;
            var perfume = await _mediator.Send(new CreatePerfumeCommand(persian, english, brand, price, total, null), ct);
            var created = await _mediator.Send(new CreateSalesListCommand(
                perfume.Id, price, total, _options.SalesChannelId, "واردشده از آرشیو کانال", minimum,
                english, productUrl, brand,
                value.TryGetProperty("gender", out var gender) ? gender.GetInt32() : 3,
                value.TryGetProperty("releaseYear", out var year) && year.ValueKind != JsonValueKind.Null ? year.GetInt32() : 0,
                persian, topNotes, middleNotes, baseNotes, accords), ct);

            var requests = value.TryGetProperty("requests", out var requestArray)
                ? JsonSerializer.Deserialize<List<ImportedRequest>>(requestArray.GetRawText()) ?? []
                : [];
            var reserved = 0;
            foreach (var request in requests.Where(value => value.Kind == SalesListRequestKind.CurrentBottle))
            {
                if (request.VolumeMl <= 0 || string.IsNullOrWhiteSpace(request.TelegramUsername)) continue;
                reserved += request.VolumeMl;
                _db.SalesListRequests.Add(new SalesListRequest
                {
                    Id = Guid.NewGuid(), CreatedAt = item.SourceDate.UtcDateTime,
                    SalesListId = created.Id, TelegramUsername = request.TelegramUsername.TrimStart('@'),
                    TelegramUserId = $"imported:{request.TelegramUsername.TrimStart('@').ToLowerInvariant()}",
                    VolumeMl = request.VolumeMl, IsBottleOwner = request.IsBottleOwner,
                    IsGift = !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername),
                    GiftRecipientTelegramUsername = request.GiftRecipientTelegramUsername,
                    Kind = request.Kind, Status = SalesListRequestStatus.Confirmed,
                    CreatedByAdmin = true, ConfirmedAt = item.SourceDate.UtcDateTime,
                    ExpiresAt = DateTime.UtcNow.AddYears(10), PerfumePricePerMl = price,
                    ExternalReference = $"telegram-import:{item.SourceChannelId}:{item.SourceMessageId}:{request.TelegramUsername}"
                });
            }
            var salesList = await _db.SalesLists.FirstAsync(value => value.Id == created.Id, ct);
            salesList.ReservedVolume = Math.Min(total, reserved);
            salesList.HasBottleOwner = requests.Any(value => value.IsBottleOwner && value.Kind == SalesListRequestKind.CurrentBottle);
            await _db.SaveChangesAsync(ct);

            item.Status = TelegramSalesListImportStatus.Imported;
            item.SalesListId = created.Id;
            item.ReviewedAt = DateTime.UtcNow;
            item.ReviewedByTelegramUserId = callback.From.Id.ToString();
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committed = true;

            if (!string.IsNullOrWhiteSpace(item.TelegramPhotoFileId) &&
                !string.IsNullOrWhiteSpace(_options.SalesChannelId))
            {
                var publishedSalesList = await _salesListRepository.GetByIdAsync(
                    item.SalesListId!.Value, ct) ?? throw new InvalidOperationException("لیست واردشده پیدا نشد.");
                var publishedRequests = await _salesListRequestRepository.GetConfirmedAsync(publishedSalesList.Id, ct);
                var published = await _sender.SendPhotoWithKeyboardAsync(
                    _options.SalesChannelId, item.TelegramPhotoFileId,
                    FormatChannelSalesList(publishedSalesList, publishedRequests),
                    BuildChannelVolumeButtons(publishedSalesList), ct);
                if (!published.IsSuccessful)
                    throw new InvalidOperationException($"ثبت انجام شد اما انتشار کانال ناموفق بود: {published.Error}");
                item.PublishedMessageId = published.MessageId;
                item.Status = TelegramSalesListImportStatus.Published;
                await _db.SaveChangesAsync(ct);
            }

            await _sender.AnswerCallbackAsync(callback.Id, "ثبت شد ✅", ct);
            await ReplyAsync(callback.Message!.Chat.Id, $"✅ لیست کد {created.PublicCode} ثبت شد. انتشار کانال بعد از کنترل نهایی انجام می‌شود.", ct);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            item.Status = TelegramSalesListImportStatus.Failed;
            item.LastError = exception.Message;
            if (!committed) await transaction.RollbackAsync(ct);
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, "ثبت ناموفق بود؛ رکورد برای بررسی باقی ماند.", ct);
            await ReplyAsync(callback.Message!.Chat.Id, exception.Message, ct);
        }
    }

    private sealed record ImportedRequest(
        string TelegramUsername, int VolumeMl, SalesListRequestKind Kind,
        bool IsBottleOwner, string? GiftRecipientTelegramUsername);

    private async Task HandleInvoicePaymentStatusCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken ct)
    {
        if (!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "فقط مدیران حسابداری مجاز به تغییر وضعیت پرداخت هستند.",
                ct,
                showAlert: true);
            return;
        }
        var parts = callback.Data!.Split(':');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "N", out var invoiceId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "شناسه فاکتور معتبر نیست.", ct, showAlert: true);
            return;
        }

        try
        {
            var result = parts[1] == "paid"
                ? await _invoicePaymentStatusService.MarkPaidAsync(invoiceId, callback.From.Id, ct)
                : await _invoicePaymentStatusService.KeepWaitingAsync(invoiceId, ct);
            var status = result.IsPaid ? "✅ پرداخت‌شده" : "⏳ در انتظار پرداخت";
            await _sender.AnswerCallbackAsync(callback.Id, $"وضعیت ثبت شد: {status}", ct, showAlert: true);
            var adminIdentity = string.IsNullOrWhiteSpace(callback.From.Username)
                ? callback.From.Id.ToString()
                : $"@{callback.From.Username.TrimStart('@')}";
            await ReplyAsync(callback.Message!.Chat.Id,
                $"{status}\nفاکتور: {result.InvoiceNumber}\nثبت توسط: {adminIdentity}", ct);
            if (result.IsPaid &&
                (!string.IsNullOrWhiteSpace(callback.Message.Text) ||
                 !string.IsNullOrWhiteSpace(callback.Message.Caption)))
            {
                var accounts = await _paymentAccountRepository.GetActiveAsync(ct);
                var paidRows = accounts.Select(account =>
                        (IReadOnlyCollection<TelegramInlineButton>)new[]
                        {
                            new TelegramInlineButton(
                                $"📋 کپی شماره کارت {account.BankName}",
                                CopyText: account.CardNumber)
                        })
                    .Append(new[]
                    {
                        new TelegramInlineButton(
                            "✅ پرداخت‌شده",
                            $"invoicepay:paid:{result.InvoiceId:N}")
                    })
                    .ToArray();
                var invoiceRefresh = !string.IsNullOrWhiteSpace(callback.Message.Caption)
                    ? await _sender.EditCaptionWithKeyboardAsync(
                        callback.Message.Chat.Id.ToString(),
                        callback.Message.MessageId,
                        callback.Message.Caption,
                        paidRows,
                        ct)
                    : await _sender.EditTextWithKeyboardAsync(
                        callback.Message.Chat.Id.ToString(),
                        callback.Message.MessageId,
                        callback.Message.Text!,
                        paidRows,
                        ct);
                if (!invoiceRefresh.IsSuccessful)
                {
                    await ReplyAsync(callback.Message.Chat.Id,
                        $"⚠️ پرداخت ثبت شد اما دکمه‌های فاکتور بروزرسانی نشد: {invoiceRefresh.Error}", ct);
                }
            }
            if (result.InvoiceIssuanceBatchId.HasValue)
            {
                var report = await _invoiceIssuanceService.GetPaymentTrackingReportAsync(
                    result.InvoiceIssuanceBatchId.Value, ct);
                if (report is not null && !string.IsNullOrWhiteSpace(report.TelegramChatId) &&
                    report.TelegramMessageId.HasValue)
                {
                    var refresh = await _sender.EditTextWithKeyboardAsync(
                        report.TelegramChatId,
                        report.TelegramMessageId.Value,
                        report.Message,
                        BuildPaymentTrackingButtons(report),
                        ct);
                    if (!refresh.IsSuccessful)
                        await ReplyAsync(callback.Message.Chat.Id,
                            $"⚠️ وضعیت مالی ثبت شد اما گزارش گروه واریز بروزرسانی نشد: {refresh.Error}", ct);
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, showAlert: true);
        }
        catch (DbUpdateConcurrencyException)
        {
            await _sender.AnswerCallbackAsync(
                callback.Id, "اطلاعات هم‌زمان تغییر کرد؛ دوباره دکمه را بزنید.", ct, showAlert: true);
        }
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildPaymentTrackingButtons(
        InvoicePaymentTrackingReport report) =>
        report.Actions.Select(action =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(action.Label,
                    $"invoiceinventory:start:{action.OrderItemId:N}")
            }).ToArray();

    private async Task HandleInvoiceInventoryCallbackAsync(
        TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id,
                "فقط مدیران حسابداری مجاز هستند.", ct, showAlert: true);
            return;
        }
        var data = callback.Data!;
        if (data == "invoiceinventory:cancel")
        {
            _invoiceInventoryDrafts.Remove(callback.Message!.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "عملیات لغو شد.", ct);
            return;
        }
        if (data == "invoiceinventory:confirm")
        {
            if (!_invoiceInventoryDrafts.TryGet(callback.Message!.Chat.Id, callback.From.Id, out var draft) ||
                !draft.NewTotalAmount.HasValue)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, showAlert: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(_options.InventoryChatId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "گروه موجودی تنظیم نشده است.", ct, showAlert: true);
                return;
            }
            try
            {
                var result = await _invoiceInventoryService.ReleaseAsync(
                    draft.OrderItemId, draft.NewTotalAmount.Value, callback.From.Id, ct);
                var publishedList = await _salesListRepository.GetByIdAsync(result.SalesListId, ct);
                if (publishedList?.TelegramMessageId.HasValue == true &&
                    !string.IsNullOrWhiteSpace(publishedList.TelegramChannelId))
                {
                    _invoiceInventoryDrafts.Remove(callback.Message!.Chat.Id, callback.From.Id);
                    await _sender.AnswerCallbackAsync(callback.Id,
                        "این آیتم قبلاً به موجودی منتقل شده است ✅", ct, showAlert: true);
                    await RefreshPaymentTrackingReportAsync(
                        result.InvoiceIssuanceBatchId, callback.Message.Chat.Id, ct);
                    return;
                }
                var caption =
                    $"🌸 <b>{System.Net.WebUtility.HtmlEncode(result.PerfumeName)}</b>\n" +
                    $"📦 موجودی آماده: {result.VolumeMl} میل\n" +
                    $"🧴 شیشه: {System.Net.WebUtility.HtmlEncode(result.BottleName)}\n" +
                    $"💰 مبلغ عطر و شیشه: {result.TotalAmount:N0} تومان\n" +
                    $"🔖 کد: {result.PublicCode}";
                var sent = await _sender.SendPhotoWithKeyboardAsync(
                    _options.InventoryChatId.Trim(), result.PhotoFileId, caption,
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton(
                            $"انتخاب {result.VolumeMl} میل — {result.TotalAmount:N0} تومان",
                            $"slv:{EncodeCompactGuid(result.SalesListId)}:{result.VolumeMl}") }
                    }, ct);
                if (!sent.IsSuccessful || !sent.MessageId.HasValue)
                    throw new InvalidOperationException($"آیتم مالی اصلاح شد اما انتشار موجودی ناموفق بود: {sent.Error}");
                var list = await _salesListRepository.GetByIdAsync(result.SalesListId, ct)
                    ?? throw new InvalidOperationException("لیست موجودی ساخته‌شده پیدا نشد.");
                list.TelegramChannelId = _options.InventoryChatId.Trim();
                list.TelegramMessageId = sent.MessageId.Value;
                list.UpdatedAt = DateTime.UtcNow;
                await _salesListRepository.SaveChangesAsync(ct);
                _invoiceInventoryDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم به موجودی منتقل شد ✅", ct, showAlert: true);
                await ReplyAsync(callback.Message.Chat.Id,
                    $"✅ {result.PerfumeName}، {result.VolumeMl} میل با مبلغ {result.TotalAmount:N0} تومان به گروه موجودی ارسال شد.", ct);
                await RefreshPaymentTrackingReportAsync(result.InvoiceIssuanceBatchId, callback.Message.Chat.Id, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, showAlert: true);
            }
            return;
        }

        var parts = data.Split(':');
        if (parts.Length != 3 || parts[1] != "start" ||
            !Guid.TryParseExact(parts[2], "N", out var itemId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "شناسه آیتم معتبر نیست.", ct, showAlert: true);
            return;
        }
        try
        {
            var preview = await _invoiceInventoryService.GetPreviewAsync(itemId, ct);
            _invoiceInventoryDrafts.Set(new TelegramInvoiceInventoryDraft
            {
                ChatId = callback.Message!.Chat.Id,
                UserId = callback.From.Id,
                OrderItemId = itemId
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                $"📤 انتقال به موجودی\nمشتری قبلی: {preview.CustomerIdentity}\n" +
                $"عطر: {preview.PerfumeName}\nحجم: {preview.VolumeMl} میل\n" +
                $"شیشه: {preview.BottleName} ({preview.BottlePrice:N0} تومان)\n" +
                $"مبلغ فعلی: {preview.CurrentAmount:N0} تومان\n\n" +
                "مبلغ نهایی جدید عطر و شیشه را وارد کنید:", ct);
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, showAlert: true);
        }
    }

    private async Task<bool> TryHandleInvoiceInventoryMessageAsync(
        TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_invoiceInventoryDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id, ct))
        {
            _invoiceInventoryDrafts.Remove(message.Chat.Id, message.From.Id);
            return true;
        }
        if (!decimal.TryParse(NormalizeNumber(message.Text ?? string.Empty),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            await ReplyAsync(message.Chat.Id, "مبلغ معتبر و مثبت وارد کنید؛ مثال: 450000", ct);
            return true;
        }
        var preview = await _invoiceInventoryService.GetPreviewAsync(draft.OrderItemId, ct);
        if (amount < preview.BottlePrice)
        {
            await ReplyAsync(message.Chat.Id,
                $"مبلغ نمی‌تواند از هزینه شیشه ({preview.BottlePrice:N0} تومان) کمتر باشد.", ct);
            return true;
        }
        draft.NewTotalAmount = amount;
        draft.ExpiresAt = DateTime.UtcNow.AddMinutes(10);
        _invoiceInventoryDrafts.Set(draft);
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
            $"آیا انتقال قطعی انجام شود؟\n{preview.PerfumeName} — {preview.VolumeMl} میل\n" +
            $"همان شیشه: {preview.BottleName}\nمبلغ جدید: {amount:N0} تومان\n\n" +
            "پس از تأیید، آیتم از فاکتور قبلی و بدهی مشتری حذف می‌شود.",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ تأیید و ارسال", "invoiceinventory:confirm"),
                    new TelegramInlineButton("❌ لغو", "invoiceinventory:cancel")
                }
            }, ct);
        return true;
    }

    private async Task RefreshPaymentTrackingReportAsync(
        Guid batchId, long fallbackChatId, CancellationToken ct)
    {
        var report = await _invoiceIssuanceService.GetPaymentTrackingReportAsync(batchId, ct);
        if (report is null || string.IsNullOrWhiteSpace(report.TelegramChatId) ||
            !report.TelegramMessageId.HasValue) return;
        var refresh = await _sender.EditTextWithKeyboardAsync(
            report.TelegramChatId, report.TelegramMessageId.Value,
            report.Message, BuildPaymentTrackingButtons(report), ct);
        if (!refresh.IsSuccessful)
            await ReplyAsync(fallbackChatId, $"⚠️ گزارش واریز بروزرسانی نشد: {refresh.Error}", ct);
    }

    private async Task SendInvoiceAdminMenuAsync(long chatId, string? notice, CancellationToken ct)
    {
        var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
        var greetingSticker = await _invoiceTelegramSettingRepository.GetGreetingStickerFileIdAsync(ct);
        var lines = accounts.Count == 0
            ? "هنوز حساب بانکی ثبت نشده است."
            : string.Join("\n\n", accounts.Select((x, i) =>
                $"{i + 1}. {(x.IsActive ? "✅" : "⛔")} {FormatCard(x.CardNumber)}\n{x.AccountHolder} — بانک {x.BankName}"));
        var buttons = accounts.SelectMany(x => new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton(x.IsActive ? "غیرفعال‌کردن" : "فعال‌کردن", $"invoiceadmin:toggle:{x.Id:N}"),
                    new TelegramInlineButton("حذف", $"invoiceadmin:delete:{x.Id:N}") }
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("➕ راهنمای افزودن حساب", "invoiceadmin:add")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🧴 لیست فروش جدید", "adminlist:new")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🧾 صدور فاکتور لیست‌های تکمیل‌شده", "invoiceadmin:batch")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("📦 مخزن انتظار عطرها", "invoiceadmin:waiting")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("✍️ صدور فاکتور دستی", "invoiceadmin:manual")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("⏭ ثبت صف بطری بعدی", "adminrequest:start:next")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("✍️ ثبت مقدار سفارشی", "adminrequest:start:custom")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🎁 ثبت دستی هدیه", "adminrequest:start:gift")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🗑 حذف تمام آیتم‌های مشتری", "adminrequest:start:removeall")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("✏️ ویرایش لیست فروش", "adminrequest:start:edit")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("👑 مدیریت صاحب و صف باتل", "adminrequest:start:queue")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🧹 پاک‌سازی لیست تکمیل‌شده", "adminrequest:start:cleanup")
        }).ToList();
        if (long.TryParse(_options.OwnerUserId, out _))
        {
            buttons.Add(new[] { new TelegramInlineButton("💰 مدیریت قیمت‌ها", "invoiceadmin:pricing") });
            buttons.Add(new[]
            {
                new TelegramInlineButton("👋 تغییر استیکر سلام", "invoiceadmin:sticker"),
                new TelegramInlineButton("🗑 حذف استیکر", "invoiceadmin:sticker-clear")
            });
        }
        var message = (notice is null ? "" : notice + "\n\n") +
            $"⚙️ تنظیمات فاکتور زیباشی\n⏱ مهلت پرداخت: ۲۴ ساعت\n👋 استیکر سلام: {(string.IsNullOrWhiteSpace(greetingSticker) ? "پیام متنی" : "فعال")}\n🏦 حساب‌ها: {accounts.Count}/4 (پیشنهاد: ۲ حساب فعال)\n\nحساب‌های بانکی:\n" + lines +
            "\n\nافزودن حساب:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک";
        message += "\n\nثبت صف بطری بعدی (فقط ادمین):\n/nextbottle کدلیست | @username | مقدارمیل";
        message += "\n\nثبت مقدار سفارشی از کامنت:\n/listrequest کدلیست | @username | مقدارمیل | نرمال یا فانتزی";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons.ToArray(), ct);
    }

    private async Task<bool> TryHandleInvoiceStickerMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null || !_invoiceStickerDrafts.IsWaiting(message.Chat.Id, message.From.Id))
            return false;
        if (!IsPrimaryOwner(message.From.Id) ||
            !await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _invoiceStickerDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }
        if (message.Sticker is null)
        {
            await ReplyAsync(message.Chat.Id, "لطفاً خودِ استیکر را ارسال کنید؛ متن یا عکس قابل قبول نیست.", ct);
            return true;
        }

        await _invoiceTelegramSettingRepository.SetGreetingStickerFileIdAsync(
            message.Sticker.FileId, message.From.Id, ct);
        _invoiceStickerDrafts.Remove(message.Chat.Id, message.From.Id);
        var preview = await _sender.SendStickerAsync(
            message.Chat.Id.ToString(), message.Sticker.FileId, ct);
        await SendInvoiceAdminMenuAsync(message.Chat.Id,
            preview.IsSuccessful
                ? "استیکر سلام ذخیره شد و پیش‌نمایش آن ارسال شد ✅"
                : $"استیکر ذخیره شد؛ ارسال پیش‌نمایش ناموفق بود: {preview.Error}", ct);
        return true;
    }

    private async Task HandleInvoiceBatchCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var userId = callback.From.Id;
        const string waitPrefix = "invoicebatch:wait:";
        if (callback.Data!.StartsWith(waitPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[waitPrefix.Length..], "N", out var waitingId))
        {
            try
            {
                await _invoiceIssuanceService.MoveCompletedListToWaitingAsync(waitingId, ct);
                if (_invoiceIssuanceDrafts.TryGet(chatId, userId, out var selection))
                    selection.Remove(waitingId);
                await _sender.AnswerCallbackAsync(callback.Id, "به مخزن انتظار منتقل شد.", ct);
                await SendInvoiceBatchSelectionAsync(chatId, userId, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }
        const string restorePrefix = "invoicebatch:restore:";
        if (callback.Data.StartsWith(restorePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[restorePrefix.Length..], "N", out var restoreId))
        {
            try
            {
                await _invoiceIssuanceService.RestoreWaitingListAsync(restoreId, ct);
                await _sender.AnswerCallbackAsync(callback.Id, "به لیست‌های آماده بازگردانده شد.", ct);
                await SendWaitingInvoiceListsAsync(chatId, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }
        const string deletePrefix = "invoicebatch:delete:";
        if (callback.Data.StartsWith(deletePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[deletePrefix.Length..], "N", out var deleteId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendInvoiceListDeleteConfirmationAsync(chatId, deleteId, false, ct);
            return;
        }
        const string waitingDeletePrefix = "invoicebatch:waitdelete:";
        if (callback.Data.StartsWith(waitingDeletePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[waitingDeletePrefix.Length..], "N", out var waitingDeleteId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendInvoiceListDeleteConfirmationAsync(chatId, waitingDeleteId, true, ct);
            return;
        }
        const string deleteConfirmPrefix = "invoicebatch:deleteconfirm:";
        const string waitingDeleteConfirmPrefix = "invoicebatch:waitdeleteconfirm:";
        var isWaitingDelete = callback.Data.StartsWith(waitingDeleteConfirmPrefix, StringComparison.Ordinal);
        var confirmationPrefix = isWaitingDelete ? waitingDeleteConfirmPrefix : deleteConfirmPrefix;
        if (callback.Data.StartsWith(confirmationPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[confirmationPrefix.Length..], "N", out var confirmedDeleteId))
        {
            try
            {
                await _invoiceIssuanceService.CancelCompletedListAsync(confirmedDeleteId, ct);
                if (_invoiceIssuanceDrafts.TryGet(chatId, userId, out var selection))
                    selection.Remove(confirmedDeleteId);
                await _sender.AnswerCallbackAsync(callback.Id, "از صف صدور فاکتور حذف شد.", ct);
                if (isWaitingDelete)
                    await SendWaitingInvoiceListsAsync(chatId, ct);
                else
                    await SendInvoiceBatchSelectionAsync(chatId, userId, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }
        if (callback.Data == "invoicebatch:manualcancel")
        {
            _manualInvoiceDrafts.Remove(chatId, userId);
            await _sender.AnswerCallbackAsync(callback.Id, "فاکتور دستی لغو شد.", ct);
            return;
        }
        if (callback.Data == "invoicebatch:manualadd")
        {
            if (!_manualInvoiceDrafts.TryGet(chatId, userId, out var manualDraft) ||
                manualDraft.Stage != TelegramManualInvoiceStage.AwaitingMoreLines)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            manualDraft.Stage = TelegramManualInvoiceStage.AwaitingLine;
            _manualInvoiceDrafts.Set(manualDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId, "نام یا شرح آیتم بعدی را وارد کنید:", ct);
            return;
        }
        if (callback.Data == "invoicebatch:manualfinish")
        {
            if (!_manualInvoiceDrafts.TryGet(chatId, userId, out var manualDraft) ||
                manualDraft.Stage != TelegramManualInvoiceStage.AwaitingMoreLines ||
                manualDraft.Lines.Count == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "حداقل یک آیتم معتبر لازم است.", ct);
                return;
            }
            manualDraft.Stage = TelegramManualInvoiceStage.AwaitingPhoto;
            _manualInvoiceDrafts.Set(manualDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId,
                "عکس محصول را ارسال کنید. این عکس پیش از فاکتور برای مشتری فرستاده می‌شود:", ct);
            return;
        }
        if (callback.Data == "invoicebatch:manualconfirm")
        {
            if (!_manualInvoiceDrafts.TryBeginIssuing(chatId, userId, out var manualDraft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct);
                return;
            }
            try
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فاکتور در حال صدور است…", ct);
                var result = await _invoiceIssuanceService.IssueManualAsync(
                    manualDraft.CustomerIdentity, manualDraft.Lines,
                    manualDraft.ProductPhotoFileId, userId.ToString(), ct);
                _manualInvoiceDrafts.Remove(chatId, userId);
                await ReplyAsync(chatId,
                    $"✅ فاکتور دستی {result.InvoiceNumbers.Single()} صادر شد.\n" +
                    "ارسال خودکار انجام می‌شود؛ در صورت نبود گروه مشتری یا خطای دائمی، مورد به گروه خطاهای فاکتور می‌رود.", ct);
            }
            catch (InvalidOperationException exception)
            {
                manualDraft.Stage = TelegramManualInvoiceStage.AwaitingConfirmation;
                _manualInvoiceDrafts.Set(manualDraft);
                _logger.LogWarning(exception,
                    "Manual invoice issuance was rejected for Telegram user {TelegramUserId}.",
                    userId);
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
                await ReplyAsync(chatId, $"⚠️ فاکتور دستی صادر نشد:\n{exception.Message}", ct);
            }
            catch (Exception exception)
            {
                manualDraft.Stage = TelegramManualInvoiceStage.AwaitingConfirmation;
                _manualInvoiceDrafts.Set(manualDraft);
                _logger.LogError(exception, "Manual invoice issuance failed for Telegram user {TelegramUserId}.", userId);
                await _sender.AnswerCallbackAsync(callback.Id, "صدور فاکتور ناموفق بود؛ جزئیات در لاگ ثبت شد.", ct, true);
                await ReplyAsync(chatId, "⚠️ صدور فاکتور دستی ناموفق بود. مدیر فنی می‌تواند جزئیات را از لاگ بررسی کند.", ct);
            }
            return;
        }
        if (callback.Data == "invoicebatch:cancel")
        {
            _invoiceIssuanceDrafts.Remove(chatId, userId);
            await _sender.AnswerCallbackAsync(callback.Id, "انتخاب لیست‌ها لغو شد.", ct);
            return;
        }
        if (callback.Data == "invoicebatch:issue")
        {
            if (!_invoiceIssuanceDrafts.TryGet(chatId, userId, out var selected) || selected.Count == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "لیستی برای صدور انتخاب نشده است.", ct);
                return;
            }
            try
            {
                await _sender.AnswerCallbackAsync(callback.Id, "صدور فاکتورها شروع شد…", ct);
                var result = await _invoiceIssuanceService.IssueCompletedListsAsync(
                    selected.ToArray(), userId.ToString(), ct);
                _invoiceIssuanceDrafts.Remove(chatId, userId);
                var productionDispatchFailures = await SendProductionCopiesAsync(result.ProductionCopies, ct);
                var paymentTrackingStatus = await SendPaymentTrackingReportAsync(result.BatchId, ct);
                var productionDispatchStatus = productionDispatchFailures.Count == 0
                    ? $"نسخهٔ عملیاتی {result.ProductionCopies.Count} لیست به گروه دکانت و گروه چاپ لیبل ارسال شد ✅"
                    : "⚠️ ارسال نسخهٔ عملیاتی کامل نشد:\n" + string.Join("\n", productionDispatchFailures);
                await ReplyAsync(chatId,
                    $"✅ {result.InvoiceCount} فاکتور تجمیعی صادر شد.\n" +
                    $"شماره‌ها: {string.Join("، ", result.InvoiceNumbers)}\n\n" +
                    productionDispatchStatus + "\n" + paymentTrackingStatus + "\n\n" +
                    "ارسال خودکار فاکتور انجام می‌شود؛ موارد بدون گروه یا با خطای دائمی در گروه خطاهای فاکتور ثبت خواهند شد.", ct);
            }
            catch (InvalidOperationException exception)
            {
                _logger.LogWarning(exception,
                    "Completed sales-list invoice issuance was rejected for Telegram user {TelegramUserId} and {SalesListCount} selected lists.",
                    userId,
                    selected.Count);
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
                await ReplyAsync(chatId, $"⚠️ فاکتور صادر نشد:\n{exception.Message}", ct);
                await SendInvoiceBatchSelectionAsync(chatId, userId, ct);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Completed sales-list invoice issuance failed for Telegram user {TelegramUserId} and {SalesListCount} selected lists.",
                    userId,
                    selected.Count);
                await _sender.AnswerCallbackAsync(callback.Id, "صدور فاکتور ناموفق بود؛ جزئیات در لاگ ثبت شد.", ct, true);
                await ReplyAsync(chatId,
                    "⚠️ صدور فاکتور انجام نشد. هیچ فاکتور قطعی ثبت نشده است؛ جزئیات خطا برای بررسی ثبت شد.", ct);
            }
            return;
        }
        const string togglePrefix = "invoicebatch:toggle:";
        if (!callback.Data!.StartsWith(togglePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(callback.Data[togglePrefix.Length..], "N", out var salesListId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "عملیات معتبر نیست.", ct);
            return;
        }
        var draft = _invoiceIssuanceDrafts.GetOrCreate(chatId, userId);
        if (!draft.Add(salesListId)) draft.Remove(salesListId);
        await _sender.AnswerCallbackAsync(callback.Id, "انتخاب به‌روزرسانی شد.", ct);
        await SendInvoiceBatchSelectionAsync(chatId, userId, ct);
    }

    private async Task<IReadOnlyCollection<string>> SendProductionCopiesAsync(
        IReadOnlyCollection<SalesListProductionCopy> copies,
        CancellationToken ct)
    {
        var failures = new List<string>();
        if (copies.Count == 0)
            return failures;

        await SendProductionCopiesToChatAsync(
            _options.DecantChatId,
            "گروه دکانت",
            copies,
            copy => copy.DecantMessage,
            failures,
            ct);
        await SendProductionCopiesToChatAsync(
            _options.LabelPrintChatId,
            "گروه چاپ لیبل",
            copies,
            copy => copy.LabelPrintMessage,
            failures,
            ct);
        return failures;
    }

    private async Task<string> SendPaymentTrackingReportAsync(Guid batchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.NewPaymentsChatId))
            return "⚠️ گروه واریز جدید تنظیم نشده است.";
        var report = await _invoiceIssuanceService.GetPaymentTrackingReportAsync(batchId, ct);
        if (report is null)
            return "⚠️ گزارش واریز ساخته نشد.";
        var sent = await _sender.SendInlineKeyboardAsync(
            _options.NewPaymentsChatId.Trim(), report.Message,
            BuildPaymentTrackingButtons(report), ct);
        if (!sent.IsSuccessful || !sent.MessageId.HasValue)
            return $"⚠️ ارسال گزارش واریز ناموفق بود: {sent.Error}";
        await _invoiceIssuanceService.SetPaymentTrackingMessageAsync(
            batchId, _options.NewPaymentsChatId.Trim(), sent.MessageId.Value, ct);
        return "گزارش وضعیت به گروه واریز جدید ارسال شد ✅";
    }

    private async Task SendProductionCopiesToChatAsync(
        string destinationChatId,
        string destinationName,
        IReadOnlyCollection<SalesListProductionCopy> copies,
        Func<SalesListProductionCopy, string> messageSelector,
        ICollection<string> failures,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationChatId))
        {
            failures.Add($"شناسهٔ {destinationName} تنظیم نشده است.");
            return;
        }

        foreach (var copy in copies)
        {
            var part = 0;
            foreach (var message in SplitTelegramMessage(messageSelector(copy)))
            {
                part++;
                var result = await _sender.SendAsync(destinationChatId, message, ct);
                if (result.IsSuccessful)
                    continue;

                var suffix = part == 1 ? string.Empty : $" (بخش {part})";
                failures.Add($"{destinationName}، لیست {copy.PublicCode}{suffix}: {result.Error ?? "خطای نامشخص"}");
                break;
            }
        }
    }

    private static IReadOnlyCollection<string> SplitTelegramMessage(string message)
    {
        const int maxLength = 3900;
        if (message.Length <= maxLength)
            return new[] { message };

        var lines = message.Split('\n');
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > maxLength)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            if (line.Length > maxLength)
            {
                for (var index = 0; index < line.Length; index += maxLength)
                    parts.Add(line.Substring(index, Math.Min(maxLength, line.Length - index)));
                continue;
            }
            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private async Task SendInvoiceBatchSelectionAsync(long chatId, long userId, CancellationToken ct)
    {
        var available = await _invoiceIssuanceService.GetCompletedListsAsync(50, ct);
        var selected = _invoiceIssuanceDrafts.GetOrCreate(chatId, userId);
        selected.IntersectWith(available.Select(list => list.SalesListId));
        if (available.Count == 0)
        {
            await ReplyAsync(chatId, "لیست تکمیل‌شدهٔ آماده برای صدور فاکتور وجود ندارد.", ct);
            return;
        }
        var rows = available.Select(list => (IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton(
                $"{(selected.Contains(list.SalesListId) ? "✅" : "⬜")} {list.PublicCode} — {list.PerfumeName} ({list.ConfirmedRequestCount} درخواست)",
                $"invoicebatch:toggle:{list.SalesListId:N}")
        }).SelectMany((row, index) => new IReadOnlyCollection<TelegramInlineButton>[]
        {
            row,
            new[]
            {
                new TelegramInlineButton("⏸ مخزن انتظار", $"invoicebatch:wait:{available.ElementAt(index).SalesListId:N}"),
                new TelegramInlineButton("🗑 حذف", $"invoicebatch:delete:{available.ElementAt(index).SalesListId:N}")
            }
        }).ToList();
        rows.Add(new[] { new TelegramInlineButton($"🧾 صدور فاکتور برای {selected.Count} لیست انتخابی", "invoicebatch:issue") });
        rows.Add(new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:cancel") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "🧾 لیست‌های تکمیل‌شده\n\nلیست‌هایی را که باید هم‌زمان فاکتور شوند انتخاب کنید. " +
            "برای هر مشتری فقط یک فاکتور تجمیعی با همه آیتم‌های همان لیست‌ها صادر می‌شود.", rows, ct);
    }

    private async Task SendWaitingInvoiceListsAsync(long chatId, CancellationToken ct)
    {
        var waiting = await _invoiceIssuanceService.GetWaitingListsAsync(50, ct);
        if (waiting.Count == 0)
        {
            await ReplyAsync(chatId, "📦 مخزن انتظار خالی است.", ct);
            return;
        }
        var rows = waiting.Select(list => (IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton(
                $"↩ {list.PublicCode} — {list.PerfumeName}",
                $"invoicebatch:restore:{list.SalesListId:N}"),
            new TelegramInlineButton("🗑 حذف", $"invoicebatch:waitdelete:{list.SalesListId:N}")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🧾 لیست‌های آماده صدور", "invoiceadmin:batch")
        }).ToArray();
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "📦 مخزن انتظار عطرها\n\nعطرهایی که فعلاً پیدا نشده‌اند اینجا می‌مانند. با دکمه بازگردانی دوباره وارد لیست صدور فاکتور می‌شوند.",
            rows,
            ct);
    }

    private async Task SendInvoiceListDeleteConfirmationAsync(
        long chatId,
        Guid salesListId,
        bool fromWaiting,
        CancellationToken ct)
    {
        var prefix = fromWaiting ? "invoicebatch:waitdeleteconfirm:" : "invoicebatch:deleteconfirm:";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "⚠️ این لیست از صف صدور فاکتور حذف و لغوشده علامت‌گذاری می‌شود؛ اطلاعات آن برای سابقه باقی می‌ماند. مطمئن هستید؟",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✅ تأیید حذف", $"{prefix}{salesListId:N}") },
                new[] { new TelegramInlineButton("❌ انصراف", fromWaiting ? "invoiceadmin:waiting" : "invoiceadmin:batch") }
            },
            ct);
    }

    private async Task<bool> TryHandleManualInvoiceMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (!_manualInvoiceDrafts.TryGet(message.Chat.Id, message.From!.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _manualInvoiceDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingPhoto)
        {
            var photo = message.Photo?.OrderByDescending(value => (long)value.Width * value.Height).FirstOrDefault();
            if (photo is null)
            {
                await ReplyAsync(message.Chat.Id, "لطفاً عکس محصول را به‌صورت Photo ارسال کنید.", ct);
                return true;
            }
            draft.ProductPhotoFileId = photo.FileId;
            draft.Stage = TelegramManualInvoiceStage.AwaitingConfirmation;
            _manualInvoiceDrafts.Set(draft);
            var total = draft.Lines.Sum(line => line.Quantity * line.UnitAmount + line.BottleAmount);
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                $"پیش‌نمایش فاکتور دستی\nمشتری: {draft.CustomerIdentity}\nتعداد ردیف: {draft.Lines.Count}\nمبلغ کل: {total:N0} تومان\nعکس محصول: دریافت شد ✅",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[] { new TelegramInlineButton("✅ صدور نهایی", "invoicebatch:manualconfirm") },
                    new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:manualcancel") }
                }, ct);
            return true;
        }
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Equals("لغو", StringComparison.OrdinalIgnoreCase) || text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _manualInvoiceDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "صدور فاکتور دستی لغو شد.", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingCustomer)
        {
            draft.CustomerIdentity = text;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLine;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id,
                "نام یا شرح آیتم اول را وارد کنید:\n\n" +
                "اگر خواستید ردیف را یکجا ثبت کنید، فرمت سریع هم فعال است:\n" +
                "عطر تست / 5 / 250000 / 30000", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingLine)
        {
            if (text.Equals("ثبت", StringComparison.OrdinalIgnoreCase))
            {
                if (draft.Lines.Count == 0)
                {
                    await ReplyAsync(message.Chat.Id, "حداقل یک ردیف اضافه کنید.", ct);
                    return true;
                }
                draft.Stage = TelegramManualInvoiceStage.AwaitingPhoto;
                _manualInvoiceDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id,
                    "عکس محصول را ارسال کنید. این عکس پیش از فاکتور برای مشتری فرستاده می‌شود:", ct);
                return true;
            }
            if (text.Contains('/'))
            {
                var values = text.Split('/', StringSplitOptions.TrimEntries);
                if (values.Length is < 3 or > 4 || string.IsNullOrWhiteSpace(values[0]) ||
                    !TryParsePositiveInt(values[1], out var quantity) ||
                    !TryParseNonNegativeDecimal(values[2], out var unitAmount) ||
                    (values.Length == 4 && !TryParseNonNegativeDecimal(values[3], out _)))
                {
                    await ReplyAsync(message.Chat.Id,
                        "فرمت معتبر نیست. نمونه: عطر تست / 5 / 250000 / 30000", ct);
                    return true;
                }
                var bottleAmount = values.Length == 4
                    ? decimal.Parse(NormalizeNumber(values[3]), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                draft.Lines.Add(new ManualInvoiceLineInput(values[0], quantity, unitAmount, bottleAmount));
                draft.Stage = TelegramManualInvoiceStage.AwaitingMoreLines;
                _manualInvoiceDrafts.Set(draft);
                await SendManualInvoiceLineDecisionAsync(message.Chat.Id, draft.Lines.Count, ct);
                return true;
            }
            draft.PendingLineDescription = text;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLineQuantity;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "مقدار یا حجم آیتم را به میل وارد کنید؛ مثال: 5", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingLineQuantity)
        {
            if (!TryParsePositiveInt(text, out var quantity))
            {
                await ReplyAsync(message.Chat.Id, "مقدار نامعتبر است؛ فقط عدد مثبت وارد کنید.", ct);
                return true;
            }
            draft.PendingLineQuantity = quantity;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLineUnitAmount;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "قیمت هر واحد یا هر میل را به تومان وارد کنید؛ مثال: 250000", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingLineUnitAmount)
        {
            if (!TryParseNonNegativeDecimal(text, out var unitAmount))
            {
                await ReplyAsync(message.Chat.Id, "قیمت واحد نامعتبر است؛ عدد صفر یا مثبت وارد کنید.", ct);
                return true;
            }
            draft.PendingLineUnitAmount = unitAmount;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLineBottleAmount;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "قیمت شیشه را به تومان وارد کنید؛ اگر رایگان است 0 بفرستید.", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingLineBottleAmount)
        {
            if (!TryParseNonNegativeDecimal(text, out var bottleAmount))
            {
                await ReplyAsync(message.Chat.Id, "قیمت شیشه نامعتبر است؛ عدد صفر یا مثبت وارد کنید.", ct);
                return true;
            }
            draft.Lines.Add(new ManualInvoiceLineInput(
                draft.PendingLineDescription,
                draft.PendingLineQuantity,
                draft.PendingLineUnitAmount,
                bottleAmount));
            draft.PendingLineDescription = string.Empty;
            draft.PendingLineQuantity = 0;
            draft.PendingLineUnitAmount = 0;
            draft.Stage = TelegramManualInvoiceStage.AwaitingMoreLines;
            _manualInvoiceDrafts.Set(draft);
            await SendManualInvoiceLineDecisionAsync(message.Chat.Id, draft.Lines.Count, ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingMoreLines)
        {
            if (text.Equals("ثبت", StringComparison.OrdinalIgnoreCase))
            {
                draft.Stage = TelegramManualInvoiceStage.AwaitingPhoto;
                _manualInvoiceDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id,
                    "عکس محصول را ارسال کنید. این عکس پیش از فاکتور برای مشتری فرستاده می‌شود:", ct);
                return true;
            }
            draft.PendingLineDescription = text;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLineQuantity;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "مقدار یا حجم آیتم را به میل وارد کنید؛ مثال: 5", ct);
            return true;
        }
        return true;
    }

    private async Task SendManualInvoiceLineDecisionAsync(long chatId, int lineCount, CancellationToken ct) =>
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            $"آیتم {lineCount} اضافه شد ✅\nآیا آیتم دیگری دارید؟",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("➕ افزودن آیتم بعدی", "invoicebatch:manualadd") },
                new[] { new TelegramInlineButton("✅ پایان و دریافت عکس", "invoicebatch:manualfinish") },
                new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:manualcancel") }
            }, ct);

    private static bool TryParseNonNegativeDecimal(string value, out decimal amount) =>
        decimal.TryParse(
            NormalizeNumber(value),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out amount) && amount >= 0;

    private async Task<bool> IsAuthorizedInvoiceAdminAsync(long chatId, long userId, CancellationToken ct) =>
        long.TryParse(_options.AdminChatId, out var configured) && configured == chatId &&
        (IsPrimaryOwner(userId) ||
         await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct));

    private async Task<bool> IsAuthorizedInvoiceActionAdminAsync(long userId, CancellationToken ct) =>
        IsPrimaryOwner(userId) ||
        (!string.IsNullOrWhiteSpace(_options.AdminChatId) &&
         await _sender.IsChatAdministratorAsync(_options.AdminChatId.Trim(), userId.ToString(), ct));

    private static bool TryNormalizeCard(string value, out string card)
    {
        card = new string(value.Where(char.IsDigit).ToArray());
        return card.Length == 16;
    }

    private static string FormatCard(string card) => string.Join('-', Enumerable.Range(0, 4).Select(i => card.Substring(i * 4, 4)));

    private bool IsPrimaryOwner(long userId) =>
        long.TryParse(_options.OwnerUserId, out var ownerUserId) && ownerUserId == userId;

    private static int[] PriceableVolumes(BottleType type, int minimum, int maximum) =>
        Enumerable.Range(minimum, maximum - minimum + 1).Where(volume =>
            (volume != 3 || type == BottleType.Normal) &&
            (volume <= 10 || type == BottleType.Fancy)).ToArray();

    private async Task SendOwnerPriceConfirmationAsync(long chatId, string preview, CancellationToken ct) =>
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "پیش‌نمایش تغییر قیمت:\n\n" + preview,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ تأیید تغییر قیمت", "ownerprice:confirm"),
                    new TelegramInlineButton("❌ لغو", "ownerprice:cancel")
                }
            }, ct);

    private async Task<bool> TryHandleOwnerPricingMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_ownerPricingDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft) ||
            draft.Stage == TelegramOwnerPricingStage.AwaitingConfirmation)
            return false;

        if (!IsPrimaryOwner(message.From.Id) ||
            !await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _ownerPricingDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }

        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return true;
        if (string.Equals(input, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _ownerPricingDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "مدیریت قیمت لغو شد.", ct);
            return true;
        }

        if (draft.Stage == TelegramOwnerPricingStage.AwaitingMinimumVolume)
        {
            if (!TryParsePositiveInt(input, out var minimum))
            {
                await ReplyAsync(message.Chat.Id, "حداقل حجم نامعتبر است؛ فقط عدد مثبت وارد کنید.", ct);
                return true;
            }
            draft.MinimumVolumeMl = minimum;
            draft.Stage = TelegramOwnerPricingStage.AwaitingMaximumVolume;
            _ownerPricingDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "حداکثر حجم شیشه را به میل وارد کنید؛ مثال: 10", ct);
            return true;
        }

        if (draft.Stage == TelegramOwnerPricingStage.AwaitingMaximumVolume)
        {
            if (!TryParsePositiveInt(input, out var maximum) || maximum < draft.MinimumVolumeMl)
            {
                await ReplyAsync(message.Chat.Id,
                    $"حداکثر حجم باید عددی مساوی یا بزرگ‌تر از {draft.MinimumVolumeMl} باشد.", ct);
                return true;
            }
            if (PriceableVolumes(draft.BottleType!.Value, draft.MinimumVolumeMl, maximum).Length == 0)
            {
                await ReplyAsync(message.Chat.Id, "در این بازه حجم استاندارد مجازی وجود ندارد؛ مقدار دیگری وارد کنید.", ct);
                return true;
            }
            draft.MaximumVolumeMl = maximum;
            draft.Stage = TelegramOwnerPricingStage.AwaitingBottlePrice;
            _ownerPricingDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "قیمت هر شیشه را به تومان وارد کنید؛ مثال: 30000", ct);
            return true;
        }

        if (draft.Stage == TelegramOwnerPricingStage.AwaitingBottlePrice)
        {
            if (!TryParsePositiveDecimal(input, out var price))
            {
                await ReplyAsync(message.Chat.Id, "قیمت نامعتبر است؛ فقط مبلغ مثبت به تومان وارد کنید.", ct);
                return true;
            }
            draft.Value = price;
            draft.Stage = TelegramOwnerPricingStage.AwaitingConfirmation;
            _ownerPricingDrafts.Set(draft);
            var affected = PriceableVolumes(draft.BottleType!.Value, draft.MinimumVolumeMl, draft.MaximumVolumeMl);
            await SendOwnerPriceConfirmationAsync(message.Chat.Id,
                $"نوع: {(draft.BottleType == BottleType.Normal ? "نرمال" : "فانتزی")}\n" +
                $"حجم‌های تحت تأثیر: {string.Join("، ", affected)} میل\nقیمت جدید هر شیشه: {price:N0} تومان", ct);
            return true;
        }

        if (draft.Stage == TelegramOwnerPricingStage.AwaitingPercentageValue)
        {
            if (!decimal.TryParse(NormalizeNumber(input), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var absolutePercent) ||
                absolutePercent <= 0 || absolutePercent > 1000 ||
                (draft.PercentageSign < 0 && absolutePercent >= 100))
            {
                await ReplyAsync(message.Chat.Id, "درصد نامعتبر است؛ یک عدد مثبت وارد کنید.", ct);
                return true;
            }
            draft.Value = absolutePercent * draft.PercentageSign;
            draft.Stage = TelegramOwnerPricingStage.AwaitingConfirmation;
            _ownerPricingDrafts.Set(draft);
            var perfumes = await _perfumeRepository.GetAllAsync(false, 200, ct);
            var samples = perfumes.Take(3).Select(value =>
                $"{value.EnglishName}: {value.PricePerMl:N0} ← {AdjustedPrice(value.PricePerMl, draft.Value):N0}");
            var openListsCount = (await _salesListRepository.GetForAdminAsync(200, ct))
                .Count(value => value.Status == SalesListStatus.Open);
            await SendOwnerPriceConfirmationAsync(message.Chat.Id,
                $"تغییر قیمت کاتالوگ {perfumes.Count} عطر: {draft.Value:+0.##;-0.##}%\n" +
                string.Join("\n", samples) +
                $"\n{openListsCount} لیست فروش باز نیز به‌روزرسانی می‌شود؛ فاکتورها و لیست‌های بسته تغییر نمی‌کنند.", ct);
            return true;
        }

        return true;
    }

    private async Task HandleAdminRequestCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var userId = callback.From.Id;
        var parts = callback.Data!.Split(':');

        if (callback.Data == "adminrequest:cancel")
        {
            _adminRequestDrafts.Remove(chatId, userId);
            await _sender.AnswerCallbackAsync(callback.Id, "لغو شد.", ct);
            await ReplyAsync(chatId, "ثبت درخواست لغو شد.", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "start")
        {
            var kind = parts[2] switch
            {
                "next" => TelegramAdminRequestKind.NextBottle,
                "edit" => TelegramAdminRequestKind.EditList,
                "cleanup" => TelegramAdminRequestKind.CleanupList,
                "queue" => TelegramAdminRequestKind.ManageBottleQueue,
                "gift" => TelegramAdminRequestKind.GiftRequest,
                "removeall" => TelegramAdminRequestKind.RemoveCustomerRequests,
                _ => TelegramAdminRequestKind.CustomRequest
            };
            _adminRequestDrafts.Set(new TelegramAdminRequestDraft
            {
                ChatId = chatId, UserId = userId, Kind = kind,
                Stage = kind == TelegramAdminRequestKind.RemoveCustomerRequests
                    ? TelegramAdminRequestStage.AwaitingIdentity
                    : TelegramAdminRequestStage.AwaitingListSearch
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (kind == TelegramAdminRequestKind.RemoveCustomerRequests)
            {
                await ReplyAsync(chatId,
                    "آیدی مشتری را به‌صورت @username یا Telegram ID وارد کنید.\n" +
                    "همه آیتم‌های فعال او در تمام لیست‌های باز نمایش داده می‌شود تا پیش از حذف تأیید کنید.", ct);
                return;
            }
            await ReplyAsync(chatId,
                "کد لیست یا بخشی از نام فارسی/انگلیسی عطر را وارد کنید:", ct);
            return;
        }

        if (parts.Length == 4 && parts[1] == "list" && Guid.TryParseExact(parts[3], "N", out var listId))
        {
            var list = (await _salesListRepository.GetForAdminAsync(200, ct)).FirstOrDefault(x => x.Id == listId);
            if (list is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "لیست پیدا نشد.", ct);
                return;
            }
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var draft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            draft.SalesListId = list.Id;
            draft.PublicCode = list.PublicCode;
            draft.SalesListName = list.EnglishName;
            draft.Stage = TelegramAdminRequestStage.AwaitingIdentity;
            _adminRequestDrafts.Set(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (draft.Kind == TelegramAdminRequestKind.CleanupList)
            {
                if (list.Status != SalesListStatus.Full)
                {
                    await ReplyAsync(chatId, "این لیست هنوز تکمیل نشده و قابل پاک‌سازی نیست.", ct);
                    return;
                }
                var requests = await _salesListRequestRepository.GetConfirmedAsync(list.Id, ct);
                await CompleteAndRollSalesListAsync(list, requests, ct);
                _adminRequestDrafts.Remove(chatId, userId);
                await ReplyAsync(chatId, "پاک‌سازی لیست تکمیل‌شده انجام شد ✅", ct);
                return;
            }
            if (draft.Kind == TelegramAdminRequestKind.EditList)
            {
                await SendEditFieldSelectionAsync(chatId, ct);
                return;
            }
            if (draft.Kind == TelegramAdminRequestKind.ManageBottleQueue)
            {
                await SendBottleQueueManagementAsync(chatId, list, ct);
                return;
            }
            await ReplyAsync(chatId,
                draft.Kind == TelegramAdminRequestKind.GiftRequest
                    ? "شناسه هدیه‌دهنده را به‌صورت @username یا Telegram ID وارد کنید:"
                    : "شناسه مشتری را به صورت @username یا Telegram ID وارد کنید.", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "editfield")
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var draft) || draft.Kind != TelegramAdminRequestKind.EditList)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            draft.EditField = parts[2];
            draft.Stage = parts[2] == "photo" ? TelegramAdminRequestStage.AwaitingEditPhoto : TelegramAdminRequestStage.AwaitingEditValue;
            _adminRequestDrafts.Set(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId, parts[2] == "photo" ? "عکس جدید عطر را ارسال کنید:" : "مقدار جدید را وارد کنید:", ct);
            return;
        }

        if (parts.Length == 4 && parts[1] == "queue" &&
            Guid.TryParseExact(parts[3], "N", out var requestId))
        {
            if (parts[2] == "edit")
            {
                if (!_adminRequestDrafts.TryGet(chatId, userId, out var queueDraft))
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                    return;
                }
                queueDraft.SelectedRequestId = requestId;
                queueDraft.Stage = TelegramAdminRequestStage.AwaitingQueueVolume;
                _adminRequestDrafts.Set(queueDraft);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(chatId, "مقدار جدید را به میل وارد کنید:", ct);
                return;
            }
            if (parts[2] == "identity")
            {
                if (!_adminRequestDrafts.TryGet(chatId, userId, out var identityDraft))
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                    return;
                }
                identityDraft.SelectedRequestId = requestId;
                identityDraft.Stage = TelegramAdminRequestStage.AwaitingQueueIdentity;
                _adminRequestDrafts.Set(identityDraft);
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(chatId, "شناسه جدید صاحب باتل را به‌صورت @username یا Telegram ID وارد کنید:", ct);
                return;
            }
            try
            {
                if (parts[2] == "promote")
                    await _salesListRequestRepository.PromoteNextBottleOwnerAsync(requestId, ct);
                else if (parts[2] == "remove")
                    await _salesListRequestRepository.RemoveConfirmedAsync(requestId, ct);
                else
                    throw new InvalidOperationException("عملیات نامعتبر است.");
                var changed = await _salesListRequestRepository.GetAsync(requestId, ct);
                if (changed is not null)
                    await RefreshChannelSalesListAsync(changed.SalesListId, ct);
                var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(auditChatId,
                    $"{(parts[2] == "promote" ? "👑 ارتقا به صاحب باتل" : "🗑 حذف از صاحب/صف باتل")}\n" +
                    $"ثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
                    $"درخواست: {requestId:N}\n" +
                    $"زمان: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran"):yyyy/MM/dd HH:mm:ss}", ct);
                await _sender.AnswerCallbackAsync(callback.Id, "انجام شد ✅", ct);
                if (changed is not null)
                    await SendBottleQueueManagementAsync(chatId, changed.SalesList, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct);
            }
            return;
        }

        if (parts.Length == 3 && parts[1] == "bottle")
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var draft) ||
                draft.Kind is not (TelegramAdminRequestKind.CustomRequest or TelegramAdminRequestKind.GiftRequest))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            draft.IsBottleOwner = parts[2] == "owner";
            draft.BottleType = draft.IsBottleOwner ? null :
                parts[2] == "fancy" ? BottleType.Fancy : BottleType.Normal;
            draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
            _adminRequestDrafts.Set(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendAdminRequestConfirmationAsync(draft, ct);
            return;
        }

        if (callback.Data == "adminrequest:confirm")
        {
            await ConfirmAdminRequestAsync(callback, ct);
            return;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct);
    }

    private async Task<bool> TryHandleAdminRequestMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !_adminRequestDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
            return false;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingEditPhoto)
        {
            var photo = message.Photo?.OrderByDescending(x => (long)x.Width * x.Height).FirstOrDefault();
            if (photo is null)
            {
                await ReplyAsync(message.Chat.Id, "لطفاً عکس را به‌صورت Photo ارسال کنید.", ct);
                return true;
            }
            draft.EditValue = photo.FileId;
            draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
            _adminRequestDrafts.Set(draft);
            await SendEditConfirmationAsync(draft, "عکس جدید", ct);
            return true;
        }
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (string.Equals(input, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ثبت درخواست لغو شد.", ct);
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingListSearch)
        {
            var query = input.Trim();
            var normalizedCode = new string(query.Where(char.IsDigit).ToArray());
            var lists = (await _salesListRepository.GetForAdminAsync(200, ct))
                .Where(x => x.Status is SalesListStatus.Open or SalesListStatus.Full)
                .Where(x =>
                    (!string.IsNullOrEmpty(normalizedCode) && x.PublicCode.ToString().Contains(normalizedCode, StringComparison.Ordinal)) ||
                    x.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.PersianName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToArray();
            if (lists.Length == 0)
            {
                await ReplyAsync(message.Chat.Id,
                    "نتیجه‌ای پیدا نشد. کد یا نام دیگری وارد کنید؛ برای لغو /cancel را بفرستید.", ct);
                return true;
            }
            var kindCode = draft.Kind switch
            {
                TelegramAdminRequestKind.NextBottle => "n",
                TelegramAdminRequestKind.EditList => "e",
                TelegramAdminRequestKind.CleanupList => "x",
                TelegramAdminRequestKind.ManageBottleQueue => "q",
                TelegramAdminRequestKind.GiftRequest => "g",
                _ => "c"
            };
            var rows = lists.Select(x => (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton($"{x.PublicCode} — {x.EnglishName}",
                    $"adminrequest:list:{kindCode}:{x.Id:N}")
            }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton("❌ لغو", "adminrequest:cancel")
            }).ToArray();
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                lists.Length == 10
                    ? "نتایج جستجو (۱۰ نتیجه اول)؛ لیست موردنظر را انتخاب کنید:"
                    : "نتیجه جستجو؛ لیست موردنظر را انتخاب کنید:", rows, ct);
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingEditValue)
        {
            draft.EditValue = input;
            draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
            _adminRequestDrafts.Set(draft);
            await SendEditConfirmationAsync(draft, input, ct);
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingQueueVolume)
        {
            if (!TryParsePositiveInt(input, out var newVolume))
            {
                await ReplyAsync(message.Chat.Id, "مقدار نامعتبر است؛ فقط عدد مثبت وارد کنید.", ct);
                return true;
            }
            try
            {
                await _salesListRequestRepository.UpdateConfirmedVolumeAsync(draft.SelectedRequestId, newVolume, ct);
                var changed = await _salesListRequestRepository.GetAsync(draft.SelectedRequestId, ct);
                if (changed is not null)
                {
                    await RefreshChannelSalesListAsync(changed.SalesListId, ct);
                    draft.Stage = TelegramAdminRequestStage.AwaitingIdentity;
                    _adminRequestDrafts.Set(draft);
                    var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                        ? _options.AdminChatId : _options.SalesAuditChatId;
                    await _sender.SendAsync(auditChatId,
                        $"✏️ ویرایش مقدار صاحب/صف باتل\nثبت‌کننده: {DisplayTelegramUser(message.From)}\n" +
                        $"درخواست: {draft.SelectedRequestId:N}\nمقدار جدید: {newVolume} میل", ct);
                    await ReplyAsync(message.Chat.Id, "مقدار با موفقیت ویرایش شد ✅", ct);
                    await SendBottleQueueManagementAsync(message.Chat.Id, changed.SalesList, ct);
                }
            }
            catch (InvalidOperationException exception)
            {
                await ReplyAsync(message.Chat.Id, exception.Message, ct);
            }
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingQueueIdentity)
        {
            try
            {
                await _salesListRequestRepository.UpdateBottleOwnerIdentityAsync(draft.SelectedRequestId, input, ct);
                var changed = await _salesListRequestRepository.GetAsync(draft.SelectedRequestId, ct);
                if (changed is not null)
                {
                    await RefreshChannelSalesListAsync(changed.SalesListId, ct);
                    draft.Stage = TelegramAdminRequestStage.AwaitingIdentity;
                    _adminRequestDrafts.Set(draft);
                    await ReplyAsync(message.Chat.Id, "شناسه صاحب باتل ویرایش شد ✅", ct);
                    await SendBottleQueueManagementAsync(message.Chat.Id, changed.SalesList, ct);
                }
            }
            catch (InvalidOperationException exception)
            {
                await ReplyAsync(message.Chat.Id, exception.Message, ct);
            }
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingIdentity)
        {
            if (draft.Kind == TelegramAdminRequestKind.RemoveCustomerRequests)
            {
                var identity = input.Trim();
                var removalIdentity = identity.StartsWith('@')
                    ? identity
                    : new string(identity.Where(char.IsDigit).ToArray());
                if ((identity.StartsWith('@') && identity.Length < 2) ||
                    (!identity.StartsWith('@') && removalIdentity.Length < 5))
                {
                    await ReplyAsync(message.Chat.Id, "شناسه نامعتبر است؛ @username یا Telegram ID وارد کنید.", ct);
                    return true;
                }
                var count = await _salesListRequestRepository.CountActiveCustomerRequestsAsync(removalIdentity, ct);
                if (count == 0)
                {
                    _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                    await ReplyAsync(message.Chat.Id,
                        "آیتم فعالی از این مشتری در لیست‌های باز پیدا نشد.", ct);
                    return true;
                }
                draft.Identity = removalIdentity;
                draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
                _adminRequestDrafts.Set(draft);
                await SendBulkCustomerRemovalConfirmationAsync(draft, count, ct);
                return true;
            }
            var identities = System.Text.RegularExpressions.Regex.Split(
                input, "\\s+for\\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var giver = identities[0].Trim();
            var normalized = giver.StartsWith('@') ? giver : new string(giver.Where(char.IsDigit).ToArray());
            if (identities.Length > 2 || (giver.StartsWith('@') && giver.Length < 2) ||
                (!giver.StartsWith('@') && normalized.Length == 0))
            {
                await ReplyAsync(message.Chat.Id, "شناسه نامعتبر است؛ @username یا Telegram ID وارد کنید.", ct);
                return true;
            }
            draft.Identity = normalized;
            draft.IsGift = draft.Kind == TelegramAdminRequestKind.GiftRequest || identities.Length == 2;
            draft.GiftRecipientIdentity = identities.Length == 2 ? identities[1].Trim() : string.Empty;
            if (draft.Kind == TelegramAdminRequestKind.GiftRequest)
            {
                draft.GiftRecipientIdentity = string.Empty;
                draft.Stage = TelegramAdminRequestStage.AwaitingGiftRecipient;
                _adminRequestDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "شناسه هدیه‌گیرنده را به‌صورت @username یا Telegram ID وارد کنید:", ct);
                return true;
            }
            if (draft.IsGift && string.IsNullOrWhiteSpace(draft.GiftRecipientIdentity))
            {
                await ReplyAsync(message.Chat.Id, "شناسه هدیه‌گیرنده خالی است؛ مثال: @giver for @recipient", ct);
                return true;
            }
            draft.Stage = TelegramAdminRequestStage.AwaitingVolume;
            _adminRequestDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id,
                draft.Kind == TelegramAdminRequestKind.NextBottle
                    ? "این مشتری چند میل از باتل اصلی می‌خواهد؟ مقدار را به میل وارد کنید؛ مثال: 30"
                    : "مقدار درخواستی را به میل وارد کنید؛ مثال: 5", ct);
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingGiftRecipient)
        {
            var recipient = input.Trim();
            if (!(recipient.StartsWith('@') && recipient.Length > 1) &&
                new string(recipient.Where(char.IsDigit).ToArray()).Length < 5)
            {
                await ReplyAsync(message.Chat.Id, "شناسه هدیه‌گیرنده نامعتبر است.", ct);
                return true;
            }
            draft.GiftRecipientIdentity = recipient;
            draft.Stage = TelegramAdminRequestStage.AwaitingVolume;
            _adminRequestDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "مقدار هدیه را به میل وارد کنید؛ مثال: 5", ct);
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingVolume)
        {
            if (!TryParsePositiveInt(input, out var volume))
            {
                await ReplyAsync(message.Chat.Id, "مقدار نامعتبر است؛ فقط عدد مثبت وارد کنید.", ct);
                return true;
            }
            draft.VolumeMl = volume;
            if (draft.Kind == TelegramAdminRequestKind.NextBottle)
            {
                draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
                _adminRequestDrafts.Set(draft);
                await SendAdminRequestConfirmationAsync(draft, ct);
            }
            else
            {
                draft.Stage = TelegramAdminRequestStage.AwaitingBottleType;
                _adminRequestDrafts.Set(draft);
                await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(), "نوع شیشه را انتخاب کنید:",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[]
                        {
                            new TelegramInlineButton("نرمال", "adminrequest:bottle:normal"),
                            new TelegramInlineButton("فانتزی", "adminrequest:bottle:fancy")
                        },
                        new[] { new TelegramInlineButton("👑 صاحب باتل — شیشه رایگان", "adminrequest:bottle:owner") },
                        new[] { new TelegramInlineButton("❌ لغو", "adminrequest:cancel") }
                    }, ct);
            }
            return true;
        }
        return true;
    }

    private async Task SendAdminRequestConfirmationAsync(TelegramAdminRequestDraft draft, CancellationToken ct)
    {
        var kind = draft.Kind == TelegramAdminRequestKind.NextBottle ? "صف بطری بعدی" : "مقدار سفارشی";
        var bottle = draft.IsBottleOwner ? "\nنوع: صاحب باتل — شیشه رایگان" : draft.BottleType is null ? "" :
            $"\nنوع شیشه: {(draft.BottleType == BottleType.Normal ? "نرمال" : "فانتزی")}";
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"پیش‌نمایش ثبت {kind}:\n\nلیست: {draft.PublicCode} — {draft.SalesListName}\n" +
            $"مشتری: {draft.Identity}{(draft.IsGift ? $" for {draft.GiftRecipientIdentity}" : string.Empty)}\nمقدار: {draft.VolumeMl} میل{bottle}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ تأیید و ثبت", "adminrequest:confirm"),
                    new TelegramInlineButton("❌ لغو", "adminrequest:cancel")
                }
            }, ct);
    }

    private async Task SendEditFieldSelectionAsync(long chatId, CancellationToken ct)
    {
        IReadOnlyCollection<TelegramInlineButton>[] rows =
        [
            [new("نام انگلیسی", "adminrequest:editfield:english"), new("نام فارسی", "adminrequest:editfield:persian")],
            [new("لینک عطردان", "adminrequest:editfield:url"), new("برند", "adminrequest:editfield:brand")],
            [new("جنسیت", "adminrequest:editfield:gender"), new("سال تولید", "adminrequest:editfield:year")],
            [new("نت ابتدایی", "adminrequest:editfield:top"), new("نت میانی", "adminrequest:editfield:middle")],
            [new("نت پایانی", "adminrequest:editfield:base"), new("آکوردها", "adminrequest:editfield:accords")],
            [new("قیمت هر میل", "adminrequest:editfield:price"), new("حداقل سفارش", "adminrequest:editfield:minimum")],
            [new("توضیحات", "adminrequest:editfield:notes"), new("🖼 تغییر عکس", "adminrequest:editfield:photo")],
            [new("❌ لغو", "adminrequest:cancel")]
        ];
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), "فیلد موردنظر برای ویرایش را انتخاب کنید:", rows, ct);
    }

    private async Task SendBottleQueueManagementAsync(long chatId, SalesList list, CancellationToken ct)
    {
        var requests = await _salesListRequestRepository.GetConfirmedAsync(list.Id, ct);
        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>();
        foreach (var request in requests.Where(value => value.IsBottleOwner))
            rows.Add(new[]
            {
                new TelegramInlineButton("🆔 ویرایش شناسه", $"adminrequest:queue:identity:{request.Id:N}"),
                new TelegramInlineButton("✏️ ویرایش مقدار", $"adminrequest:queue:edit:{request.Id:N}"),
                new TelegramInlineButton($"🗑 حذف صاحب: {DisplayUser(request)} — {request.VolumeMl} میل",
                    $"adminrequest:queue:remove:{request.Id:N}")
            });
        foreach (var request in requests.Where(value => value.Kind == SalesListRequestKind.NextBottle))
            rows.Add(new[]
            {
                new TelegramInlineButton($"👑 ارتقا: {DisplayUser(request)} — {request.VolumeMl} میل",
                    $"adminrequest:queue:promote:{request.Id:N}"),
                new TelegramInlineButton("✏️", $"adminrequest:queue:edit:{request.Id:N}"),
                new TelegramInlineButton("حذف", $"adminrequest:queue:remove:{request.Id:N}")
            });
        rows.Add(new[] { new TelegramInlineButton("❌ بستن", "adminrequest:cancel") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            $"مدیریت صاحب و صف باتل\nلیست {list.PublicCode} — {list.EnglishName}\n" +
            (rows.Count == 1 ? "صاحب یا فردی در صف ثبت نشده است." : "عملیات موردنظر را انتخاب کنید:"),
            rows, ct);
    }

    private async Task SendEditConfirmationAsync(TelegramAdminRequestDraft draft, string displayValue, CancellationToken ct) =>
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"پیش‌نمایش ویرایش لیست {draft.PublicCode} — {draft.SalesListName}\n\nمقدار جدید: {displayValue}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✅ ذخیره", "adminrequest:confirm"), new TelegramInlineButton("❌ لغو", "adminrequest:cancel") }
            }, ct);

    private async Task SendBulkCustomerRemovalConfirmationAsync(
        TelegramAdminRequestDraft draft, int requestCount, CancellationToken ct) =>
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            "⚠️ تأیید حذف همه آیتم‌های مشتری\n\n" +
            $"مشتری: {draft.Identity}\n" +
            $"تعداد آیتم فعال در همه لیست‌های باز: {requestCount}\n\n" +
            "با تأیید، درخواست‌ها لغو، حجم لیست‌ها اصلاح و پست‌های کانال به‌روز می‌شوند.",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("🗑 بله، همه حذف شوند", "adminrequest:confirm"),
                    new TelegramInlineButton("❌ لغو", "adminrequest:cancel")
                }
            }, ct);

    private async Task ConfirmAdminRequestAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!_adminRequestDrafts.TryGet(chatId, callback.From.Id, out var draft) ||
            draft.Stage != TelegramAdminRequestStage.AwaitingConfirmation)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct);
            return;
        }
        if (draft.Kind == TelegramAdminRequestKind.EditList)
        {
            await ApplySalesListEditAsync(callback, draft, ct);
            return;
        }
        if (draft.Kind == TelegramAdminRequestKind.RemoveCustomerRequests)
        {
            var affectedListIds = await _salesListRequestRepository.RemoveAllActiveCustomerRequestsAsync(
                draft.Identity, ct);
            if (affectedListIds.Count == 0)
            {
                _adminRequestDrafts.Remove(chatId, callback.From.Id);
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم فعالی باقی نمانده است.", ct);
                await ReplyAsync(chatId, "حذفی انجام نشد؛ آیتم فعال دیگری برای این مشتری وجود ندارد.", ct);
                return;
            }
            foreach (var salesListId in affectedListIds)
                await RefreshChannelSalesListAsync(salesListId, ct);
            var removalAuditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                ? _options.AdminChatId : _options.SalesAuditChatId;
            await _sender.SendAsync(removalAuditChatId,
                "🗑 حذف تمام آیتم‌های مشتری\n" +
                $"زمان: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran"):yyyy/MM/dd HH:mm:ss}\n" +
                $"ثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
                $"مشتری: {draft.Identity}\n" +
                $"تعداد لیست‌های به‌روزشده: {affectedListIds.Count}", ct);
            _adminRequestDrafts.Remove(chatId, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "همه آیتم‌های فعال حذف شدند ✅", ct);
            await ReplyAsync(chatId, "همه آیتم‌های فعال مشتری حذف و لیست‌های درگیر به‌روزرسانی شدند ✅", ct);
            return;
        }
        var identity = draft.Identity.Trim();
        var username = identity.StartsWith('@') ? identity.TrimStart('@') : null;
        var telegramId = username is null ? identity : $"admin-username:{username.ToLowerInvariant()}";
        Bottle? bottle = null;
        if (draft.Kind is (TelegramAdminRequestKind.CustomRequest or TelegramAdminRequestKind.GiftRequest) && !draft.IsBottleOwner)
        {
            var bottles = await _mediator.Send(
                new ZibasheERP.Application.Features.Bottles.GetAvailableBottles.GetAvailableBottlesQuery(draft.VolumeMl), ct);
            var summary = bottles.FirstOrDefault(x => string.Equals(x.Type, draft.BottleType.ToString(), StringComparison.OrdinalIgnoreCase));
            if (summary is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "برای این حجم، شیشه فعال از نوع انتخاب‌شده وجود ندارد.", ct);
                return;
            }
            bottle = await _bottleRepository.GetByIdAsync(summary.Id, ct);
        }
        var list = (await _salesListRepository.GetForAdminAsync(200, ct)).FirstOrDefault(x => x.Id == draft.SalesListId);
        if (list is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "لیست پیدا نشد.", ct);
            return;
        }
        var request = new SalesListRequest
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, SalesListId = list.Id,
            TelegramUserId = telegramId, TelegramUsername = username, VolumeMl = draft.VolumeMl,
            IsGift = draft.IsGift,
            GiftRecipientTelegramUsername = draft.IsGift && draft.GiftRecipientIdentity.StartsWith('@')
                ? draft.GiftRecipientIdentity.TrimStart('@') : null,
            GiftRecipientTelegramUserId = draft.IsGift && !draft.GiftRecipientIdentity.StartsWith('@')
                ? new string(draft.GiftRecipientIdentity.Where(char.IsDigit).ToArray()) : null,
            IsBottleOwner = draft.IsBottleOwner,
            BottleId = bottle?.Id, PerfumePricePerMl = list.PricePerMl, BottlePrice = bottle?.SalePrice ?? 0,
            Kind = draft.Kind == TelegramAdminRequestKind.NextBottle
                ? SalesListRequestKind.NextBottle : SalesListRequestKind.CurrentBottle,
            Status = draft.Kind == TelegramAdminRequestKind.NextBottle
                ? SalesListRequestStatus.Confirmed : SalesListRequestStatus.PendingConfirmation,
            CreatedByAdmin = true,
            ExpiresAt = draft.Kind == TelegramAdminRequestKind.NextBottle ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(10),
            ConfirmedAt = draft.Kind == TelegramAdminRequestKind.NextBottle ? DateTime.UtcNow : null,
            ExternalReference = $"admin-interactive:{Guid.NewGuid():N}"
        };
        await _salesListRequestRepository.AddAsync(request, ct);
        await _salesListRequestRepository.SaveChangesAsync(ct);
        if (draft.Kind is TelegramAdminRequestKind.CustomRequest or TelegramAdminRequestKind.GiftRequest)
            await _salesListRequestRepository.ConfirmCurrentBottleAsync(request.Id, telegramId, ct);
        await RefreshChannelSalesListAsync(list.Id, ct);
        var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
            ? _options.AdminChatId
            : _options.SalesAuditChatId;
        var tehranNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran");
        var adminIdentity = DisplayTelegramUser(callback.From);
        if (draft.Kind is TelegramAdminRequestKind.CustomRequest or TelegramAdminRequestKind.GiftRequest)
        {
            var bottleLabel = draft.IsBottleOwner
                ? "صاحب باتل — رایگان"
                : bottle is null
                ? "نامشخص"
                : $"{BottleLabel(bottle.Type.ToString())} — {bottle.SalePrice:N0} تومان";
            var total = draft.VolumeMl * list.PricePerMl + (bottle?.SalePrice ?? 0);
            await _sender.SendAsync(auditChatId,
                (draft.IsGift ? "🎁 ثبت دستی هدیه در لیست فروش\n" : "✍️ ثبت دستی در لیست فروش\n") +
                $"زمان: {tehranNow:yyyy/MM/dd HH:mm:ss}\n" +
                $"ثبت‌کننده: {adminIdentity}\n" +
                $"مشتری: {draft.Identity}{(draft.IsGift ? $" for {draft.GiftRecipientIdentity}" : string.Empty)}\n" +
                $"کد لیست: {list.PublicCode}\n" +
                $"عطر: {list.EnglishName}\n" +
                $"مقدار: {draft.VolumeMl} میل\n" +
                $"شیشه: {bottleLabel}\n" +
                $"مبلغ کل: {total:N0} تومان", ct);
        }
        else
        {
            await _sender.SendAsync(auditChatId,
                "⏭ ثبت دستی در صف Next Bottle\n" +
                $"زمان: {tehranNow:yyyy/MM/dd HH:mm:ss}\n" +
                $"ثبت‌کننده: {adminIdentity}\n" +
                $"مشتری: {draft.Identity}\n" +
                $"کد لیست: {list.PublicCode}\n" +
                $"عطر: {list.EnglishName}\n" +
                $"مقدار درخواستی از باتل اصلی: {draft.VolumeMl} میل", ct);
        }
        _adminRequestDrafts.Remove(chatId, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست ثبت شد ✅", ct);
        await ReplyAsync(chatId, "درخواست با موفقیت ثبت و لیست فروش به‌روزرسانی شد ✅", ct);
    }

    private async Task ApplySalesListEditAsync(TelegramCallbackQuery callback, TelegramAdminRequestDraft draft, CancellationToken ct)
    {
        var list = await _salesListRepository.GetByIdAsync(draft.SalesListId, ct);
        if (list is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "لیست پیدا نشد.", ct);
            return;
        }
        try
        {
            switch (draft.EditField)
            {
                case "english": list.EnglishName = draft.EditValue.Trim(); break;
                case "persian": list.PersianName = draft.EditValue.Trim(); break;
                case "url" when Uri.TryCreate(draft.EditValue, UriKind.Absolute, out _): list.ProductPageUrl = draft.EditValue.Trim(); break;
                case "url": throw new InvalidOperationException("لینک معتبر نیست.");
                case "brand": list.DisplayBrand = draft.EditValue.Trim(); break;
                case "gender": list.Gender = draft.EditValue.Trim() switch { "زنانه" or "women" => PerfumeGender.Women, "مردانه" or "men" => PerfumeGender.Men, "یونیسکس" or "unisex" => PerfumeGender.Unisex, _ => throw new InvalidOperationException("جنسیت باید زنانه، مردانه یا یونیسکس باشد.") }; break;
                case "year" when int.TryParse(NormalizeNumber(draft.EditValue), out var year) && year is >= 1800 and <= 2200: list.ReleaseYear = year; break;
                case "year": throw new InvalidOperationException("سال تولید معتبر نیست.");
                case "top": list.TopNotes = draft.EditValue.Trim(); break;
                case "middle": list.MiddleNotes = draft.EditValue.Trim(); break;
                case "base": list.BaseNotes = draft.EditValue.Trim(); break;
                case "accords": list.Accords = draft.EditValue.Trim(); break;
                case "price" when TryParsePositiveDecimal(draft.EditValue, out var price): list.PricePerMl = price; break;
                case "price": throw new InvalidOperationException("قیمت معتبر نیست.");
                case "minimum" when TryParsePositiveInt(draft.EditValue, out var minimum) && minimum <= list.TotalVolume: list.MinimumRequestVolumeMl = minimum; break;
                case "minimum": throw new InvalidOperationException("حداقل سفارش معتبر نیست.");
                case "notes": list.Notes = draft.EditValue == "-" ? null : draft.EditValue.Trim(); break;
                case "photo": list.TelegramPhotoFileId = draft.EditValue; break;
                default: throw new InvalidOperationException("فیلد ویرایش معتبر نیست.");
            }
            list.UpdatedAt = DateTime.UtcNow;
            await _salesListRepository.UpdateAsync(list, ct);
            await _salesListRepository.SaveChangesAsync(ct);
            var requests = await _salesListRequestRepository.GetConfirmedAsync(list.Id, ct);
            if (list.TelegramMessageId.HasValue && !string.IsNullOrWhiteSpace(list.TelegramChannelId))
            {
                if (draft.EditField == "photo")
                    await _sender.EditPhotoAsync(list.TelegramChannelId, list.TelegramMessageId.Value, draft.EditValue,
                        FormatChannelSalesList(list, requests), BuildChannelVolumeButtons(list), ct);
                else
                    await RefreshChannelSalesListAsync(list.Id, ct);
            }
            _adminRequestDrafts.Remove(callback.Message!.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "ویرایش ذخیره شد ✅", ct);
            await ReplyAsync(callback.Message.Chat.Id, "لیست و پست کانال با موفقیت به‌روزرسانی شدند ✅", ct);
        }
        catch (InvalidOperationException ex)
        {
            await _sender.AnswerCallbackAsync(callback.Id, ex.Message, ct);
        }
    }

    private async Task ApplyOwnerPricingDraftAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (!_ownerPricingDrafts.TryGet(callback.Message!.Chat.Id, callback.From.Id, out var draft))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct);
            return;
        }
        if (draft.Kind == TelegramOwnerPricingKind.BottleRange)
        {
            var affected = PriceableVolumes(draft.BottleType!.Value, draft.MinimumVolumeMl, draft.MaximumVolumeMl);
            var existing = await _bottleRepository.GetForAdminAsync(true, 200, ct);
            foreach (var volume in affected)
            {
                var bottle = existing.FirstOrDefault(value =>
                    value.VolumeMl == volume && value.Type == draft.BottleType.Value);
                if (bottle is null)
                {
                    await _bottleRepository.AddAsync(new Bottle
                    {
                        Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
                        Name = $"شیشه {(draft.BottleType == BottleType.Normal ? "نرمال" : "فانتزی")} {volume} میل",
                        VolumeMl = volume, Type = draft.BottleType.Value,
                        SalePrice = draft.Value, IsActive = true
                    }, ct);
                }
                else
                {
                    bottle.SalePrice = draft.Value;
                    bottle.IsActive = true;
                    bottle.IsDeleted = false;
                    bottle.UpdatedAt = DateTime.UtcNow;
                    await _bottleRepository.UpdateAsync(bottle, ct);
                }
            }
            await _bottleRepository.SaveChangesAsync(ct);
        }
        else
        {
            var perfumes = await _perfumeRepository.GetAllAsync(false, 200, ct);
            foreach (var summary in perfumes)
            {
                var perfume = await _perfumeRepository.GetByIdAsync(summary.Id, ct);
                if (perfume is null) continue;
                perfume.PricePerMl = AdjustedPrice(perfume.PricePerMl, draft.Value);
                perfume.UpdatedAt = DateTime.UtcNow;
                await _perfumeRepository.UpdateAsync(perfume, ct);
            }
            await _perfumeRepository.SaveChangesAsync(ct);

            var openLists = (await _salesListRepository.GetForAdminAsync(200, ct))
                .Where(value => value.Status == SalesListStatus.Open)
                .ToArray();
            foreach (var summary in openLists)
            {
                var list = await _salesListRepository.GetByIdAsync(summary.Id, ct);
                if (list is null) continue;
                list.PricePerMl = AdjustedPrice(list.PricePerMl, draft.Value);
                list.UpdatedAt = DateTime.UtcNow;
                await _salesListRepository.UpdateAsync(list, ct);
            }
            await _salesListRepository.SaveChangesAsync(ct);
            foreach (var list in openLists.Where(value => value.TelegramMessageId.HasValue))
                await RefreshChannelSalesListAsync(list.Id, ct);
        }
        _ownerPricingDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, "تغییر قیمت اعمال شد ✅", ct);
        await ReplyAsync(callback.Message.Chat.Id, "تغییر قیمت با موفقیت اعمال شد ✅", ct);
    }

    private static decimal AdjustedPrice(decimal price, decimal percent) =>
        Math.Round(price * (1 + percent / 100m), 0, MidpointRounding.AwayFromZero);
}
