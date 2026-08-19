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

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From!.Id, ct))
        {
            await ReplyAsync(message.Chat.Id, "این بخش فقط برای مدیران گروه حسابداری فعال است.", ct);
            return true;
        }

        if (text.Equals("/whoami", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(message.Chat.Id, $"Telegram User ID شما: {message.From.Id}", ct);
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
                    MinimumVolumeMl = minimum, MaximumVolumeMl = maximum, Value = price
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
                Kind = TelegramOwnerPricingKind.PerfumePercentage, Value = percent
            });
            var samples = perfumes.Take(3).Select(value =>
                $"{value.EnglishName}: {value.PricePerMl:N0} ← {AdjustedPrice(value.PricePerMl, percent):N0}");
            await SendOwnerPriceConfirmationAsync(message.Chat.Id,
                $"تغییر قیمت کاتالوگ {perfumes.Count} عطر: {percent:+0.##;-0.##}%\n" +
                string.Join("\n", samples) +
                "\nقیمت لیست‌های منتشرشده تغییر نمی‌کند.", ct);
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
              callback.Data.StartsWith("ownerprice:", StringComparison.Ordinal)))
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
            await ApplyOwnerPricingDraftAsync(callback, ct);
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
            await ReplyAsync(callback.Message.Chat.Id,
                "مدیریت گروهی قیمت‌ها:\n\n" +
                "/bottleprice نرمال | 5 | 10 | 30000\n" +
                "/bottleprice فانتزی | 5 | 10 | 60000\n\n" +
                "افزایش ۵ درصد قیمت کاتالوگ عطرها:\n/perfumepercent +5\n" +
                "کاهش ۵ درصد:\n/perfumepercent -5", ct);
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
        }).ToList();
        if (long.TryParse(_options.OwnerUserId, out _))
            buttons.Add(new[] { new TelegramInlineButton("💰 راهنمای مدیریت قیمت‌ها", "invoiceadmin:pricing") });
        var message = (notice is null ? "" : notice + "\n\n") +
            $"⚙️ تنظیمات فاکتور زیباشی\n⏱ مهلت پرداخت: ۲۴ ساعت\n🏦 حساب‌ها: {accounts.Count}/4 (پیشنهاد: ۲ حساب فعال)\n\nحساب‌های بانکی:\n" + lines +
            "\n\nافزودن حساب:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک";
        message += "\n\nثبت صف بطری بعدی (فقط ادمین):\n/nextbottle کدلیست | @username | مقدارمیل";
        message += "\n\nثبت مقدار سفارشی از کامنت:\n/listrequest کدلیست | @username | مقدارمیل | نرمال یا فانتزی";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons.ToArray(), ct);
    }

    private async Task<bool> IsAuthorizedInvoiceAdminAsync(long chatId, long userId, CancellationToken ct) =>
        long.TryParse(_options.AdminChatId, out var configured) && configured == chatId &&
        await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct);

    private static bool TryNormalizeCard(string value, out string card)
    {
        card = new string(value.Where(char.IsDigit).ToArray());
        return card.Length == 16;
    }

    private static string FormatCard(string card) => string.Join('-', Enumerable.Range(0, 4).Select(i => card.Substring(i * 4, 4)));

    private bool IsPrimaryOwner(long userId) =>
        long.TryParse(_options.OwnerUserId, out var ownerUserId) && ownerUserId == userId;

    private static readonly int[] StandardVolumes = [1, 2, 3, 4, 5, 7, 10, 15, 20, 30, 50];
    private static int[] PriceableVolumes(BottleType type, int minimum, int maximum) =>
        StandardVolumes.Where(volume => volume >= minimum && volume <= maximum &&
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
        }
        _ownerPricingDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, "تغییر قیمت اعمال شد ✅", ct);
        await ReplyAsync(callback.Message.Chat.Id, "تغییر قیمت با موفقیت اعمال شد ✅", ct);
    }

    private static decimal AdjustedPrice(decimal price, decimal percent) =>
        Math.Round(price * (1 + percent / 100m), 0, MidpointRounding.AwayFromZero);
}
