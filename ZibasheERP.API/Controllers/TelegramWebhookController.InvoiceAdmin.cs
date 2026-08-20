using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

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
            !(callback.Data.StartsWith("invoiceadmin:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("ownerprice:", StringComparison.Ordinal) ||
              callback.Data.StartsWith("adminrequest:", StringComparison.Ordinal)))
            return false;
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

    private async Task SendInvoiceAdminMenuAsync(long chatId, string? notice, CancellationToken ct)
    {
        var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
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
            new TelegramInlineButton("⏭ ثبت صف بطری بعدی", "adminrequest:start:next")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("✍️ ثبت مقدار سفارشی", "adminrequest:start:custom")
        }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton("🎁 ثبت دستی هدیه", "adminrequest:start:gift")
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
            buttons.Add(new[] { new TelegramInlineButton("💰 مدیریت قیمت‌ها", "invoiceadmin:pricing") });
        var message = (notice is null ? "" : notice + "\n\n") +
            $"⚙️ تنظیمات فاکتور زیباشی\n⏱ مهلت پرداخت: ۲۴ ساعت\n🏦 حساب‌ها: {accounts.Count}/4 (پیشنهاد: ۲ حساب فعال)\n\nحساب‌های بانکی:\n" + lines +
            "\n\nافزودن حساب:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک";
        message += "\n\nثبت صف بطری بعدی (فقط ادمین):\n/nextbottle کدلیست | @username | مقدارمیل";
        message += "\n\nثبت مقدار سفارشی از کامنت:\n/listrequest کدلیست | @username | مقدارمیل | نرمال یا فانتزی";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons.ToArray(), ct);
    }

    private async Task<bool> IsAuthorizedInvoiceAdminAsync(long chatId, long userId, CancellationToken ct) =>
        long.TryParse(_options.AdminChatId, out var configured) && configured == chatId &&
        (IsPrimaryOwner(userId) ||
         await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct));

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
                _ => TelegramAdminRequestKind.CustomRequest
            };
            _adminRequestDrafts.Set(new TelegramAdminRequestDraft
            {
                ChatId = chatId, UserId = userId, Kind = kind,
                Stage = TelegramAdminRequestStage.AwaitingListSearch
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
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
