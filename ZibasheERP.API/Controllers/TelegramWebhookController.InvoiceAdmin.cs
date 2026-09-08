using ZibasheERP.API.Telegram;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using System.Collections.Concurrent;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static readonly ConcurrentDictionary<(long ChatId, long UserId), TelegramSalesListImportEditDraft> ImportEditDrafts = new();

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
            message.From is not null &&
            await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            await ReplyAsync(message.Chat.Id, $"Telegram User ID دریافت‌شده توسط ربات: {message.From!.Id}", ct);
            return true;
        }

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From!.Id, ct))
        {
            await ReplyAsync(message.Chat.Id,
                "این بخش فقط برای مدیران گروه حسابداری، داخل گروه مدیریت یا چت خصوصی ربات فعال است.", ct);
            return true;
        }

        var command = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .Split('@', 2)[0];
        if (string.Equals(command, "/admin", StringComparison.OrdinalIgnoreCase))
        {
            ClearAdminWorkflowDrafts(message.Chat.Id, message.From.Id);
            await SendInvoiceAdminMenuAsync(message.Chat.Id, null, ct);
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
            var perfumes = await _perfumeRepository.GetAllActiveForPriceUpdateAsync(ct);
            _ownerPricingDrafts.Set(new TelegramOwnerPricingDraft
            {
                ChatId = message.Chat.Id, UserId = message.From.Id,
                Kind = TelegramOwnerPricingKind.PerfumePercentage, Value = percent,
                Stage = TelegramOwnerPricingStage.AwaitingConfirmation
            });
            var samples = perfumes.Take(3).Select(value =>
                $"{value.EnglishName}: {value.PricePerMl:N0} ← {AdjustedPrice(value.PricePerMl, percent):N0}");
            var openListsCount = await _salesListRepository.CountAllOpenAsync(ct);
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
              callback.Data.StartsWith("orderflow:", StringComparison.Ordinal) ||
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

        if (callback.Data.StartsWith("orderflow:", StringComparison.Ordinal))
        {
            await HandleOrderFlowCallbackAsync(callback, ct);
            return true;
        }

        if (callback.Data.StartsWith("invoiceadmin:menu:", StringComparison.Ordinal))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            var section = callback.Data["invoiceadmin:menu:".Length..];
            if (section == "main")
                await SendInvoiceAdminMenuAsync(callback.Message.Chat.Id, null, ct);
            else
                await SendInvoiceAdminSectionAsync(callback.Message.Chat.Id, section, callback.From.Id, ct);
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

        if (callback.Data == "invoiceadmin:rebuild-sales-lists")
        {
            if (!IsPrimaryOwner(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فقط مدیر سیستم مجاز است.", ct, showAlert: true);
                return true;
            }
            var queued = _salesListRebuildWorker.TryQueue(callback.Message.Chat.Id);
            await _sender.AnswerCallbackAsync(callback.Id,
                queued ? "بازسازی پست‌ها در صف قرار گرفت ✅" : "بازسازی از قبل در حال اجرا یا در صف است.", ct,
                showAlert: !queued);
            if (queued)
                await ReplyAsync(callback.Message.Chat.Id,
                    "بازسازی همه پست‌های فعال کانال در پس‌زمینه شروع می‌شود. پس از پایان، گزارش ارسال خواهد شد.", ct);
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
        if (callback.Data == "invoiceadmin:edit-caption")
        {
            _invoiceCaptionEditDrafts.Set(new TelegramInvoiceCaptionEditDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "آیدی مشتری را وارد کنید؛ مثال: @zahraa_frj\nحداکثر ۳ فاکتور آخر نمایش داده می‌شود.\n\nبرای لغو، /cancel را بفرستید.", ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:resend")
        {
            _invoiceResendDrafts.Set(callback.Message.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "آیدی مشتری را وارد کنید؛ مثال: @zahraa_frj\nحداکثر ۳ فاکتور آخر نمایش داده می‌شود.\n\nبرای لغو، /cancel را بفرستید.", ct);
            return true;
        }
        if (callback.Data.StartsWith("invoiceadmin:resend:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["invoiceadmin:resend:".Length..], "N", out var resendInvoiceId))
        {
            if (!_invoiceResendDrafts.IsWaiting(callback.Message.Chat.Id, callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return true;
            }
            var invoice = await _db.Invoices
                .Include(value => value.Order).ThenInclude(value => value!.Customer)
                .ThenInclude(value => value!.TelegramGroup)
                .FirstOrDefaultAsync(value => value.Id == resendInvoiceId && !value.IsDeleted, ct);
            var destination = invoice?.Order?.Customer?.TelegramGroup;
            if (invoice is null || string.IsNullOrWhiteSpace(invoice.TelegramInvoiceChatId) ||
                !invoice.TelegramInvoiceMessageId.HasValue)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "PDF قبلی این فاکتور در دسترس نیست.", ct, true);
                return true;
            }
            if (destination is null || destination.IsDeleted || !destination.IsActive ||
                string.IsNullOrWhiteSpace(destination.ChatId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "گروه فعال مشتری متصل نیست.", ct, true);
                return true;
            }
            var paymentAccounts = await _paymentAccountRepository.GetActiveAsync(ct);
            var rows = new List<IReadOnlyCollection<TelegramInlineButton>>
            {
                new TelegramInlineButton[]
                {
                    new("✅ پرداخت‌شده", $"invoicepay:paid:{invoice.Id:N}"),
                    new("⏳ در انتظار پرداخت", $"invoicepay:waiting:{invoice.Id:N}")
                }
            };
            foreach (var account in paymentAccounts.Take(4))
                rows.Add(new TelegramInlineButton[]
                {
                    new($"📋 کپی شماره کارت {account.BankName}".Trim(), CopyText: account.CardNumber)
                });
            var resend = await _sender.CopyMessageWithKeyboardAsync(
                destination.ChatId.Trim(), invoice.TelegramInvoiceChatId,
                invoice.TelegramInvoiceMessageId.Value, rows, ct);
            if (!resend.IsSuccessful || !resend.MessageId.HasValue)
            {
                await _sender.AnswerCallbackAsync(callback.Id,
                    $"ارسال مجدد انجام نشد: {resend.Error ?? "پیام PDF پیدا نشد."}", ct, true);
                return true;
            }
            invoice.TelegramInvoiceChatId = destination.ChatId.Trim();
            invoice.TelegramInvoiceMessageId = resend.MessageId.Value;
            invoice.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _invoiceResendDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "فاکتور مجدداً ارسال شد ✅", ct, true);
            await ReplyAsync(callback.Message.Chat.Id,
                $"فاکتور {invoice.InvoiceNumber} مجدداً به گروه مشتری ارسال شد ✅", ct);
            return true;
        }
        if (callback.Data.StartsWith("invoiceadmin:caption:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["invoiceadmin:caption:".Length..], "N", out var captionInvoiceId))
        {
            if (!_invoiceCaptionEditDrafts.TryGet(callback.Message.Chat.Id, callback.From.Id, out var captionDraft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return true;
            }
            var invoice = await _db.Invoices.FirstOrDefaultAsync(
                value => value.Id == captionInvoiceId && !value.IsDeleted &&
                    !string.IsNullOrWhiteSpace(value.TelegramInvoiceChatId) &&
                    value.TelegramInvoiceMessageId.HasValue,
                ct);
            if (invoice is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "PDF این فاکتور قابل ویرایش نیست.", ct, showAlert: true);
                return true;
            }
            captionDraft.InvoiceId = invoice.Id;
            captionDraft.InvoiceNumber = invoice.InvoiceNumber;
            captionDraft.Stage = TelegramInvoiceCaptionEditStage.AwaitingCaption;
            _invoiceCaptionEditDrafts.Set(captionDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                $"کپشن جدید برای {invoice.InvoiceNumber} را بفرستید. حداکثر ۱۰۲۴ کاراکتر.\nبرای لغو، /cancel را بفرستید.", ct);
            return true;
        }
        if (callback.Data == "invoiceadmin:manual")
        {
            _manualInvoiceDrafts.Set(new TelegramManualInvoiceDraft
            {
                ChatId = callback.Message.Chat.Id, UserId = callback.From.Id
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                "🧾 صدور فاکتور دستی\n\nآیا این فاکتور هدیه است؟",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[] { new TelegramInlineButton("🎁 بله، هدیه است", "invoicebatch:manualgift:yes") },
                    new[] { new TelegramInlineButton("خیر", "invoicebatch:manualgift:no") },
                    new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:manualcancel") }
                }, ct);
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
            await ReplyAsync(callback.Message.Chat.Id, "از این پس پیام «سلام 👋» ارسال می‌شود.", ct);
            await SendInvoiceAdminSectionAsync(callback.Message.Chat.Id, "settings", callback.From.Id, ct);
            return true;
        }

        if (callback.Data == "invoiceadmin:add")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "برای افزودن حساب این دستور را بفرستید:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک", ct);
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
        await SendInvoiceAdminSectionAsync(callback.Message.Chat.Id, "settings", callback.From.Id, ct);
        return true;
    }

    private async Task HandleSalesListImportCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var callbackMessage = callback.Message!;
        var parts = callback.Data!.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length != 3 && parts.Length != 4) ||
            !Guid.TryParseExact(parts[^1], "N", out var importId))
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
            await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id, ct) ||
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
            case "next":
            {
                var next = await _db.TelegramSalesListImports
                    .Where(value => !value.IsDeleted &&
                                    value.Status == TelegramSalesListImportStatus.PendingReview &&
                                    value.Id != item.Id)
                    .OrderBy(value => value.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (next is null)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "لیست آمادهٔ دیگری در صف نیست.", ct);
                    return;
                }

                var nextButtons = new[]
                {
                    new[]
                    {
                        new TelegramInlineButton("✅ تأیید", $"import:approve:{next.Id:N}"),
                        new TelegramInlineButton("✏️ ویرایش", $"import:edit:{next.Id:N}"),
                        new TelegramInlineButton("❌ رد", $"import:reject:{next.Id:N}")
                    }
                };
                if (!string.IsNullOrWhiteSpace(next.TelegramPhotoFileId))
                    await _sender.SendPhotoWithKeyboardAsync(
                        callbackMessage.Chat.Id.ToString(), next.TelegramPhotoFileId,
                        BuildReviewText(next, next.ParsedPayload),
                        Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
                foreach (var chunk in SplitForTelegram(next.RawText, 3800))
                    await _sender.SendInlineKeyboardAsync(callbackMessage.Chat.Id.ToString(), chunk, nextButtons, ct);
                await _sender.AnswerCallbackAsync(callback.Id, "لیست بعدی ارسال شد.", ct);
                return;
            }
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
                await _sender.AnswerCallbackAsync(callback.Id, "فیلد موردنظر را انتخاب کنید.", ct);
                await SendImportEditMenuAsync(callbackMessage.Chat.Id, item.Id, ct);
                break;
            case "field" when parts.Length == 4:
                if (!ImportEditFields.TryGetValue(parts[2], out var prompt))
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "فیلد ویرایش نامعتبر است.", ct);
                    return;
                }
                ImportEditDrafts[(callbackMessage.Chat.Id, callback.From.Id)] = new TelegramSalesListImportEditDraft(
                    item.Id, parts[2], DateTime.UtcNow.AddMinutes(10));
                await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
                await ReplyAsync(callbackMessage.Chat.Id, prompt, ct);
                break;
            case "editdone":
                ImportEditDrafts.TryRemove((callbackMessage.Chat.Id, callback.From.Id), out _);
                await _sender.AnswerCallbackAsync(callback.Id, "ویرایش‌ها ذخیره شد.", ct);
                await _sender.SendInlineKeyboardAsync(callbackMessage.Chat.Id.ToString(),
                    BuildReviewText(item, item.ParsedPayload), BuildImportReviewButtons(item.Id), ct);
                break;
            case "approve":
                if (item.Status != TelegramSalesListImportStatus.PendingReview &&
                    item.Status != TelegramSalesListImportStatus.NeedsEditing)
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "وضعیت این مورد قابل تأیید نیست.", ct);
                    return;
                }
                if (NeedsBottleOwnerDecision(item.ParsedPayload))
                {
                    var ownerButtons = new[]
                    {
                        new[]
                        {
                            new TelegramInlineButton("✅ بیشترین میل صاحب باتل است", $"import:owner:auto:{item.Id:N}"),
                            new TelegramInlineButton("⏭️ بدون صاحب باتل", $"import:owner:none:{item.Id:N}")
                        }
                    };
                    await _sender.SendInlineKeyboardAsync(
                        callbackMessage.Chat.Id.ToString(),
                        "صاحب باتل در دادهٔ اصلی مشخص نشده است. انتخاب کنید:",
                        ownerButtons,
                        ct);
                    await _sender.AnswerCallbackAsync(callback.Id, "ابتدا وضعیت صاحب باتل را انتخاب کنید.", ct);
                    return;
                }
                await ImportApprovedSalesListAsync(item, callback, ct);
                break;
            case "owner" when parts.Length == 4 && parts[2] is "auto" or "none":
                if (parts[2] == "auto")
                    SetHighestVolumeBottleOwner(item);
                await _db.SaveChangesAsync(ct);
                await ImportApprovedSalesListAsync(item, callback, ct);
                break;
            default:
                await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct);
                break;
        }
    }

    private static bool NeedsBottleOwnerDecision(string parsedPayload)
    {
        using var json = JsonDocument.Parse(parsedPayload);
        if (!json.RootElement.TryGetProperty("requests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array)
            return false;
        var currentBottleRequests = requests.EnumerateArray().Where(request =>
            request.TryGetProperty("kind", out var kind) &&
            kind.TryGetInt32(out var kindValue) &&
            kindValue == (int)SalesListRequestKind.CurrentBottle).ToArray();
        return currentBottleRequests.Length > 0 && !currentBottleRequests.Any(request =>
            request.TryGetProperty("isBottleOwner", out var owner) && owner.ValueKind == JsonValueKind.True);
    }

    private static readonly IReadOnlyDictionary<string, string> ImportEditFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["english"] = "نام انگلیسی جدید عطر را وارد کنید.",
            ["persian"] = "نام فارسی جدید عطر را وارد کنید.",
            ["brand"] = "برند جدید را وارد کنید.",
            ["price"] = "قیمت هر میل جدید را فقط به تومان وارد کنید. مثال: 250000",
            ["total"] = "حجم کل جدید را فقط به میل وارد کنید. مثال: 100",
            ["minimum"] = "حداقل میل درخواستی جدید را وارد کنید. مثال: 5",
            ["notes"] = CombinedNotesPrompt,
            ["accords"] = "آکوردهای اصلی جدید را وارد کنید."
        };

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildImportReviewButtons(Guid id) =>
    [
        [new TelegramInlineButton("✅ تأیید", $"import:approve:{id:N}"),
         new TelegramInlineButton("✏️ ویرایش", $"import:edit:{id:N}"),
         new TelegramInlineButton("❌ رد", $"import:reject:{id:N}")]
    ];

    private async Task SendImportEditMenuAsync(long chatId, Guid importId, CancellationToken ct)
    {
        var buttons = new[]
        {
            new[] { new TelegramInlineButton("نام انگلیسی", $"import:field:english:{importId:N}"), new TelegramInlineButton("نام فارسی", $"import:field:persian:{importId:N}") },
            new[] { new TelegramInlineButton("برند", $"import:field:brand:{importId:N}"), new TelegramInlineButton("قیمت هر میل", $"import:field:price:{importId:N}") },
            new[] { new TelegramInlineButton("حجم کل", $"import:field:total:{importId:N}"), new TelegramInlineButton("حداقل میل", $"import:field:minimum:{importId:N}") },
            new[] { new TelegramInlineButton("نت‌ها", $"import:field:notes:{importId:N}"), new TelegramInlineButton("آکوردها", $"import:field:accords:{importId:N}") },
            new[] { new TelegramInlineButton("✅ پایان و تأیید", $"import:editdone:{importId:N}") }
        };
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), "فیلد موردنظر را انتخاب کنید. پس از هر ثبت می‌توانید فیلد دیگری را ویرایش کنید.", buttons, ct);
    }

    private async Task<bool> TryHandleSalesListImportEditMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !ImportEditDrafts.TryGetValue((message.Chat.Id, message.From.Id), out var draft))
            return false;
        if (draft.ExpiresAt <= DateTime.UtcNow)
        {
            ImportEditDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "زمان ویرایش تمام شد؛ دوباره دکمه «ویرایش» را بزنید.", ct);
            return true;
        }
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
            return false;
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            await ReplyAsync(message.Chat.Id, "مقدار را به صورت متن ارسال کنید.", ct);
            return true;
        }
        var item = await _db.TelegramSalesListImports.FirstOrDefaultAsync(value => value.Id == draft.ImportId && !value.IsDeleted, ct);
        if (item is null || item.Status is TelegramSalesListImportStatus.Imported or TelegramSalesListImportStatus.Rejected)
        {
            ImportEditDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
            await ReplyAsync(message.Chat.Id, "این لیست دیگر قابل ویرایش نیست.", ct);
            return true;
        }
        var root = JsonNode.Parse(item.ParsedPayload)?.AsObject();
        if (root is null)
        {
            await ReplyAsync(message.Chat.Id, "دادهٔ لیست قابل ویرایش نیست.", ct);
            return true;
        }
        try
        {
            switch (draft.Field)
            {
                case "english": root["englishName"] = RequireImportEditText(input); break;
                case "persian": root["persianName"] = RequireImportEditText(input); break;
                case "brand": root["displayBrand"] = RequireImportEditText(input); break;
                case "notes" when TryParseCombinedNotes(input, out var top, out var middle, out var bottom):
                    root["topNotes"] = top;
                    root["middleNotes"] = middle;
                    root["baseNotes"] = bottom;
                    break;
                case "accords": root["accords"] = input; break;
                case "price" when TryParseNonNegativeDecimal(input, out var price) && price > 0: root["pricePerMl"] = price; break;
                case "total" when int.TryParse(NormalizeNumber(input), out var total) && total > 0: root["totalVolumeMl"] = total; break;
                case "minimum" when int.TryParse(NormalizeNumber(input), out var minimum) && minimum > 0: root["minimumRequestVolumeMl"] = minimum; break;
                default: throw new InvalidOperationException("مقدار واردشده معتبر نیست.");
            }
        }
        catch (InvalidOperationException exception)
        {
            await ReplyAsync(message.Chat.Id, exception.Message, ct);
            return true;
        }
        item.ParsedPayload = root.ToJsonString();
        item.ReviewedAt = DateTime.UtcNow;
        item.ReviewedByTelegramUserId = message.From.Id.ToString();
        await _db.SaveChangesAsync(ct);
        ImportEditDrafts.TryRemove((message.Chat.Id, message.From.Id), out _);
        await ReplyAsync(message.Chat.Id, "ویرایش ذخیره شد ✅", ct);
        await SendImportEditMenuAsync(message.Chat.Id, item.Id, ct);
        return true;
    }

    private static string RequireImportEditText(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("این فیلد نمی‌تواند خالی باشد.") : value.Trim();

    private static void SetHighestVolumeBottleOwner(TelegramSalesListImport item)
    {
        var root = JsonNode.Parse(item.ParsedPayload)?.AsObject()
            ?? throw new JsonException("ساختار درخواست‌های واردات نامعتبر است.");
        if (root["requests"] is not JsonArray requests || requests.Count == 0) return;
        var selected = requests
            .OfType<JsonObject>()
            .Where(request => request["kind"]?.GetValue<int>() ==
                              (int)SalesListRequestKind.CurrentBottle)
            .OrderByDescending(request => request["volumeMl"]?.GetValue<int>() ?? 0)
            .FirstOrDefault();
        if (selected is null) return;
        foreach (var request in requests.OfType<JsonObject>())
            request["isBottleOwner"] = ReferenceEquals(request, selected);
        item.ParsedPayload = root.ToJsonString();
    }

    private static IEnumerable<string> SplitForTelegram(string value, int maximum)
    {
        var text = value.Trim();
        while (text.Length > maximum)
        {
            var cut = text.LastIndexOf('\n', maximum - 1);
            if (cut < maximum / 2) cut = maximum;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }

    private static string BuildReviewText(TelegramSalesListImport item, string parsedPayload)
    {
        using var json = JsonDocument.Parse(parsedPayload);
        var parsed = json.RootElement;
        var code = parsed.TryGetProperty("publicCode", out var codeValue) ? codeValue.ToString() : "-";
        var name = parsed.TryGetProperty("englishName", out var nameValue) ? nameValue.GetString() : "-";
        return $"🔎 بررسی واردات لیست فروش\nکد: {code}\nعطر: {name}\nپیام مبدأ: {item.SourceMessageId}";
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
            var english = value.GetProperty("englishName").GetString()?.Trim();
            var brand = value.GetProperty("displayBrand").GetString()?.Trim();
            var persian = value.GetProperty("persianName").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(english))
                throw new InvalidOperationException("نام انگلیسی ناقص است؛ از گزینه «ویرایش» آن را وارد کنید.");
            if (string.IsNullOrWhiteSpace(brand))
                throw new InvalidOperationException("برند ثبت نشده است؛ از گزینه «ویرایش» برند را وارد کنید.");
            if (string.IsNullOrWhiteSpace(persian))
                throw new InvalidOperationException("نام فارسی ثبت نشده است؛ از گزینه «ویرایش» آن را وارد کنید.");
            var topNotes = value.TryGetProperty("topNotes", out var top) ? top.GetString() ?? string.Empty : string.Empty;
            var middleNotes = value.TryGetProperty("middleNotes", out var middle) ? middle.GetString() ?? string.Empty : string.Empty;
            var baseNotes = value.TryGetProperty("baseNotes", out var bottom) ? bottom.GetString() ?? string.Empty : string.Empty;
            var accords = value.TryGetProperty("accords", out var accordValue) ? accordValue.GetString() ?? string.Empty : string.Empty;
            var productUrl = value.TryGetProperty("productPageUrl", out var urlValue) ? urlValue.GetString() : null;
            if (!value.TryGetProperty("pricePerMl", out var priceValue) ||
                !priceValue.TryGetDecimal(out var price) || price <= 0)
                throw new InvalidOperationException("قیمت هر میل ثبت نشده است؛ از گزینه «ویرایش» قیمت را وارد کنید.");
            if (!value.TryGetProperty("totalVolumeMl", out var totalValue) ||
                !totalValue.TryGetInt32(out var total) || total <= 0)
                throw new InvalidOperationException("حجم کل ثبت نشده است؛ از گزینه «ویرایش» حجم کل را وارد کنید.");
            if (!value.TryGetProperty("minimumRequestVolumeMl", out var minimumValue) ||
                !minimumValue.TryGetInt32(out var minimum) || minimum <= 0)
                throw new InvalidOperationException("حداقل درخواست ثبت نشده است؛ از گزینه «ویرایش» حداقل میل را وارد کنید.");
            // A perfume may already exist in the catalog (including one created
            // during a previous migration test). Imported archive entries must
            // create a new sales list for that perfume instead of failing while
            // trying to create the catalog entry again.
            var normalizedEnglish = english;
            var normalizedBrand = brand;
            var existingPerfume = await _db.Perfumes.FirstOrDefaultAsync(value =>
                !value.IsDeleted &&
                value.Brand == normalizedBrand &&
                value.EnglishName == normalizedEnglish, ct);
            if (existingPerfume is not null && existingPerfume.PricePerMl > 0)
                price = existingPerfume.PricePerMl;
            var perfumeId = existingPerfume?.Id;
            if (perfumeId is null)
            {
                var perfume = await _mediator.Send(new CreatePerfumeCommand(
                    persian, normalizedEnglish, normalizedBrand, price, total, null), ct);
                perfumeId = perfume.Id;
            }
            var created = await _mediator.Send(new CreateSalesListCommand(
                perfumeId.Value, price, total, _options.SalesChannelId, "واردشده از آرشیو کانال", minimum,
                english, productUrl, brand,
                value.TryGetProperty("gender", out var gender) ? gender.GetInt32() : 3,
                value.TryGetProperty("releaseYear", out var year) && year.ValueKind != JsonValueKind.Null ? year.GetInt32() : 0,
                persian, topNotes, middleNotes, baseNotes, accords), ct);

            var requests = value.TryGetProperty("requests", out var requestArray)
                ? JsonSerializer.Deserialize<List<ImportedRequest>>(requestArray.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
                : [];
            var reserved = 0;
            foreach (var (request, requestIndex) in requests.Select((request, index) => (request, index)))
            {
                if (request.VolumeMl <= 0 || string.IsNullOrWhiteSpace(request.TelegramUsername)) continue;
                if (request.Kind == SalesListRequestKind.CurrentBottle)
                    reserved += request.VolumeMl;
                var normalizedUsername = request.TelegramUsername.Trim().TrimStart('@');
                var normalizedGiftRecipient = string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
                    ? null
                    : request.GiftRecipientTelegramUsername.Trim().TrimStart('@');
                Bottle? bottle = null;
                if (request.IsFancyBottle && request.Kind == SalesListRequestKind.CurrentBottle &&
                    !request.IsBottleOwner)
                {
                    var bottles = await _mediator.Send(
                        new ZibasheERP.Application.Features.Bottles.GetAvailableBottles.GetAvailableBottlesQuery(
                            request.VolumeMl), ct);
                    var fancy = bottles.FirstOrDefault(candidate =>
                        string.Equals(candidate.Type, nameof(BottleType.Fancy), StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(request.FancyBottleVariant) ||
                         candidate.Name.Contains(request.FancyBottleVariant,
                             StringComparison.OrdinalIgnoreCase)));
                    if (fancy is null)
                        throw new InvalidOperationException(
                            $"شیشه فانتزی{(string.IsNullOrWhiteSpace(request.FancyBottleVariant) ? "" : $" {request.FancyBottleVariant}")} " +
                            $"برای حجم {request.VolumeMl} میل پیدا نشد.");
                    bottle = await _bottleRepository.GetByIdAsync(fancy.Id, ct);
                }
                _db.SalesListRequests.Add(new SalesListRequest
                {
                    // Keep the original message order when several imported requests share one source timestamp.
                    Id = Guid.NewGuid(), CreatedAt = item.SourceDate.UtcDateTime.AddTicks(requestIndex),
                    SalesListId = created.Id, TelegramUsername = normalizedUsername,
                    TelegramUserId = request.IsExternalIdentity
                        ? $"imported-external:{normalizedUsername.ToLowerInvariant()}"
                        : $"imported:{normalizedUsername.ToLowerInvariant()}",
                    VolumeMl = request.VolumeMl, IsBottleOwner = request.IsBottleOwner,
                    OmitIdentityOnLabel = request.OmitIdentityOnLabel,
                    IsGift = normalizedGiftRecipient is not null,
                    GiftRecipientTelegramUsername = normalizedGiftRecipient,
                    GiftRecipientTelegramUserId = normalizedGiftRecipient is null
                        ? null
                        : request.GiftRecipientIsExternalIdentity
                            ? $"imported-external:{normalizedGiftRecipient.ToLowerInvariant()}"
                            : $"imported:{normalizedGiftRecipient.ToLowerInvariant()}",
                    BottleId = bottle?.Id,
                    BottlePrice = bottle?.SalePrice ?? 0,
                    Kind = request.Kind, Status = SalesListRequestStatus.Confirmed,
                    CreatedByAdmin = true, ConfirmedAt = item.SourceDate.UtcDateTime.AddTicks(requestIndex),
                    ExpiresAt = DateTime.UtcNow.AddYears(10), PerfumePricePerMl = price,
                    ExternalReference = $"telegram-import:{item.SourceChannelId}:{item.SourceMessageId}:{requestIndex}"
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
                // Imported lists must carry the same Telegram metadata as lists
                // created through the normal channel flow so customer callbacks
                // can find and update the published post.
                publishedSalesList.TelegramChannelId = _options.SalesChannelId;
                publishedSalesList.TelegramMessageId = published.MessageId;
                publishedSalesList.TelegramPhotoFileId = item.TelegramPhotoFileId;
                var discussionText =
                    $"💬 هر سؤالی در رابطه با عطر «{publishedSalesList.EnglishName}» دارید، اینجا بپرسید.\n" +
                    "اگر مقدار موردنظر شما در دکمه‌ها نیست، آن را در کامنت بنویسید تا ادمین ثبت کند.";
                var discussion = await _sender.SendReplyAsync(
                    _options.SalesChannelId, discussionText, published.MessageId!.Value, ct);
                if (discussion.IsSuccessful)
                    publishedSalesList.TelegramDiscussionMessageId = discussion.MessageId;
                await _db.SaveChangesAsync(ct);
                item.PublishedMessageId = published.MessageId;
                item.Status = TelegramSalesListImportStatus.Published;
                await _db.SaveChangesAsync(ct);
                await SendRemainingVolumeAlertsAsync(publishedSalesList, ct);
            }

            await _sender.AnswerCallbackAsync(callback.Id, "ثبت شد ✅", ct);
            await ReplyAsync(callback.Message!.Chat.Id, $"✅ لیست کد {created.PublicCode} ثبت شد. انتشار کانال بعد از کنترل نهایی انجام می‌شود.", ct);
            var nextButton = new[]
            {
                new[] { new TelegramInlineButton("📥 لیست بعدی", $"import:next:{item.Id:N}") }
            };
            await _sender.SendInlineKeyboardAsync(
                callback.Message!.Chat.Id.ToString(),
                "برای بررسی مورد بعدی روی دکمهٔ زیر بزنید:",
                nextButton,
                ct);
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

    private sealed record TelegramSalesListImportEditDraft(
        Guid ImportId, string Field, DateTime ExpiresAt);

    private sealed record ImportedRequest(
        string TelegramUsername, int VolumeMl, SalesListRequestKind Kind,
        bool IsBottleOwner, string? GiftRecipientTelegramUsername,
        bool IsExternalIdentity = false,
        bool GiftRecipientIsExternalIdentity = false,
        bool IsFancyBottle = false,
        string? FancyBottleVariant = null,
        bool OmitIdentityOnLabel = false);

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
                if (!invoiceRefresh.IsSuccessful && !IsTelegramMessageUnchanged(invoiceRefresh.Error))
                {
                    await ReplyAsync(callback.Message.Chat.Id,
                        $"⚠️ پرداخت ثبت شد اما دکمه‌های فاکتور بروزرسانی نشد: {invoiceRefresh.Error}", ct);
                }
            }
            if (result.InvoiceIssuanceBatchId.HasValue)
            {
                var reports = await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(
                    result.InvoiceIssuanceBatchId.Value, ct);
                foreach (var report in reports.Where(report =>
                             !string.IsNullOrWhiteSpace(report.TelegramChatId) &&
                             report.TelegramMessageId.HasValue))
                {
                    var refresh = await _sender.EditTextWithKeyboardAsync(
                        report.TelegramChatId!,
                        report.TelegramMessageId!.Value,
                        report.Message,
                        BuildPaymentTrackingButtons(report),
                        ct);
                    if (!refresh.IsSuccessful && !IsTelegramMessageUnchanged(refresh.Error))
                        await ReplyAsync(callback.Message.Chat.Id,
                            $"⚠️ وضعیت مالی ثبت شد اما گزارش گروه واریز بروزرسانی نشد: {refresh.Error}", ct);
                }
            }
            await RefreshManualPaymentTrackingReportAsync(result.InvoiceId, callback.Message.Chat.Id, ct);
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
        var reports = await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(batchId, ct);
        foreach (var report in reports.Where(report =>
                     !string.IsNullOrWhiteSpace(report.TelegramChatId) &&
                     report.TelegramMessageId.HasValue))
        {
            var refresh = await _sender.EditTextWithKeyboardAsync(
                report.TelegramChatId!, report.TelegramMessageId!.Value,
                report.Message, BuildPaymentTrackingButtons(report), ct);
            if (!refresh.IsSuccessful && !IsTelegramMessageUnchanged(refresh.Error))
                await ReplyAsync(fallbackChatId,
                    $"⚠️ گزارش واریز لیست مربوطه بروزرسانی نشد: {refresh.Error}", ct);
        }
    }

    private async Task SendInvoiceAdminMenuAsync(long chatId, string? notice, CancellationToken ct)
    {
        var message = (notice is null ? "" : notice + "\n\n") +
            "⚙️ مدیریت زیباشی\n\nبخش موردنظر را انتخاب کنید:";
        IReadOnlyCollection<TelegramInlineButton>[] buttons =
        {
            new[]
            {
                new TelegramInlineButton("🧾 فاکتورها", "invoiceadmin:menu:invoices"),
                new TelegramInlineButton("🧴 لیست‌های فروش", "invoiceadmin:menu:lists")
            },
            new[]
            {
                new TelegramInlineButton("👥 آیتم‌ها و صف", "invoiceadmin:menu:items"),
                new TelegramInlineButton("⚙️ تنظیمات", "invoiceadmin:menu:settings")
            },
            new[] { new TelegramInlineButton("📦 وضعیت سفارش‌ها", "orderflow:dashboard") }
        };
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons, ct);
    }

    private async Task SendInvoiceAdminSectionAsync(long chatId, string section, long userId, CancellationToken ct)
    {
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        string message;
        switch (section)
        {
            case "invoices":
                message = "🧾 مدیریت فاکتورها";
                buttons.Add(new[] { new TelegramInlineButton("صدور فاکتور لیست‌های تکمیل‌شده", "invoiceadmin:batch") });
                buttons.Add(new[] { new TelegramInlineButton("✍️ صدور فاکتور دستی", "invoiceadmin:manual") });
                buttons.Add(new[]
                {
                    new TelegramInlineButton("📦 مخزن انتظار", "invoiceadmin:waiting"),
                    new TelegramInlineButton("🔁 ارسال مجدد", "invoiceadmin:resend")
                });
                buttons.Add(new[]
                {
                    new TelegramInlineButton("✏️ ویرایش کپشن PDF", "invoiceadmin:edit-caption"),
                    new TelegramInlineButton("📸 عکس دکانت", "decantphoto:start")
                });
                break;
            case "lists":
                message = "🧴 مدیریت لیست‌های فروش";
                buttons.Add(new[] { new TelegramInlineButton("➕ لیست فروش جدید", "adminlist:new") });
                buttons.Add(new[] { new TelegramInlineButton("✏️ ویرایش لیست فروش", "adminrequest:start:edit") });
                buttons.Add(new[] { new TelegramInlineButton("🧹 پاک‌سازی لیست تکمیل‌شده", "adminrequest:start:cleanup") });
                if (IsPrimaryOwner(userId))
                    buttons.Add(new[] { new TelegramInlineButton("🔄 بازسازی همه پست‌های لیست", "invoiceadmin:rebuild-sales-lists") });
                break;
            case "items":
                message = "👥 مدیریت آیتم‌ها و صف باتل";
                buttons.Add(new[]
                {
                    new TelegramInlineButton("✍️ ثبت آیتم دستی", "adminrequest:start:custom"),
                    new TelegramInlineButton("🎁 ثبت هدیه", "adminrequest:start:gift")
                });
                buttons.Add(new[]
                {
                    new TelegramInlineButton("⏭ ثبت صف بعدی", "adminrequest:start:next"),
                    new TelegramInlineButton("👑 صاحب و صف باتل", "adminrequest:start:queue")
                });
                buttons.Add(new[] { new TelegramInlineButton("↕️ تغییر میل آیتم", "adminrequest:start:changevolume") });
                buttons.Add(new[]
                {
                    new TelegramInlineButton("🗑 حذف یک آیتم", "adminrequest:start:removeitem"),
                    new TelegramInlineButton("☑️ حذف چند آیتم", "adminrequest:start:removemultiple")
                });
                buttons.Add(new[] { new TelegramInlineButton("🗑 حذف همه آیتم‌های مشتری", "adminrequest:start:removeall") });
                buttons.Add(new[]
                {
                    new TelegramInlineButton("🏷 حذف آیدی از لیبل", "adminrequest:start:labelnoid"),
                    new TelegramInlineButton("✏️ نام دلخواه لیبل", "adminrequest:start:labeltext")
                });
                break;
            case "settings":
            {
                var accounts = await _paymentAccountRepository.GetForAdminAsync(ct);
                var greetingSticker = await _invoiceTelegramSettingRepository.GetGreetingStickerFileIdAsync(ct);
                var accountLines = accounts.Count == 0
                    ? "هنوز حساب بانکی ثبت نشده است."
                    : string.Join("\n\n", accounts.Select((x, i) =>
                        $"{i + 1}. {(x.IsActive ? "✅" : "⛔")} {FormatCard(x.CardNumber)}\n{x.AccountHolder} — بانک {x.BankName}"));
                message = $"⚙️ تنظیمات فاکتور\n👋 استیکر سلام: {(string.IsNullOrWhiteSpace(greetingSticker) ? "پیام متنی" : "فعال")}\n🏦 حساب‌ها: {accounts.Count}/4\n\n{accountLines}\n\nافزودن حساب:\n/bankadd شماره‌کارت | نام صاحب حساب | نام بانک";
                foreach (var account in accounts)
                    buttons.Add(new[]
                    {
                        new TelegramInlineButton(account.IsActive ? "غیرفعال‌کردن" : "فعال‌کردن", $"invoiceadmin:toggle:{account.Id:N}"),
                        new TelegramInlineButton("حذف", $"invoiceadmin:delete:{account.Id:N}")
                    });
                buttons.Add(new[] { new TelegramInlineButton("➕ راهنمای افزودن حساب", "invoiceadmin:add") });
                if (IsPrimaryOwner(userId))
                {
                    buttons.Add(new[] { new TelegramInlineButton("💰 مدیریت قیمت‌ها", "invoiceadmin:pricing") });
                    buttons.Add(new[]
                    {
                        new TelegramInlineButton("👋 تغییر استیکر", "invoiceadmin:sticker"),
                        new TelegramInlineButton("🗑 حذف استیکر", "invoiceadmin:sticker-clear")
                    });
                }
                break;
            }
            default:
                await SendInvoiceAdminMenuAsync(chatId, null, ct);
                return;
        }
        buttons.Add(new[] { new TelegramInlineButton("↩️ بازگشت به منوی اصلی", "invoiceadmin:menu:main") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons, ct);
    }

    private async Task<bool> TryHandleInvoiceCaptionEditMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null ||
            !_invoiceCaptionEditDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;

        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _invoiceCaptionEditDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "دسترسی مدیریت ندارید.", ct);
            return true;
        }

        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(message.Chat.Id, "لطفاً متن را به‌صورت پیام متنی ارسال کنید.", ct);
            return true;
        }
        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _invoiceCaptionEditDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ویرایش کپشن لغو شد.", ct);
            return true;
        }

        if (draft.Stage == TelegramInvoiceCaptionEditStage.AwaitingCustomerIdentity)
        {
            var customerIdentity = text.Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(customerIdentity))
            {
                await ReplyAsync(message.Chat.Id, "آیدی مشتری معتبر نیست. مثال: @zahraa_frj", ct);
                return true;
            }
            var invoices = await _db.Invoices
                .Include(value => value.Order)
                .ThenInclude(value => value!.Customer)
                .Where(value => !value.IsDeleted &&
                    value.Order != null && value.Order.Customer != null &&
                    (value.Order.Customer.Username == customerIdentity ||
                     value.Order.Customer.TelegramId == customerIdentity))
                .OrderByDescending(value => value.IssuedAt)
                .Take(3)
                .Select(value => new
                {
                    value.Id,
                    value.InvoiceNumber,
                    value.IssuedAt,
                    CanEdit = value.TelegramInvoiceChatId != null && value.TelegramInvoiceMessageId != null
                })
                .ToListAsync(ct);
            if (invoices.Count == 0)
            {
                await ReplyAsync(message.Chat.Id,
                    "برای این آیدی فاکتوری پیدا نشد. آیدی را بدون فاصله و با یا بدون @ وارد کنید.", ct);
                return true;
            }
            var buttons = invoices.Select(invoice =>
                (IReadOnlyCollection<TelegramInlineButton>)new[]
                {
                    new TelegramInlineButton(
                        $"{invoice.InvoiceNumber} — {invoice.IssuedAt:yyyy/MM/dd}" +
                        (invoice.CanEdit ? string.Empty : " (قدیمی/غیرقابل‌ویرایش)"),
                        $"invoiceadmin:caption:{invoice.Id:N}")
                }).ToArray();
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                "یکی از حداکثر سه فاکتور آخر را انتخاب کنید:", buttons, ct);
            return true;
        }

        if (text.Length > 1024)
        {
            await ReplyAsync(message.Chat.Id, "کپشن بیشتر از ۱۰۲۴ کاراکتر است. متن کوتاه‌تری بفرستید.", ct);
            return true;
        }

        var targetInvoice = await _db.Invoices.FirstOrDefaultAsync(
            value => value.Id == draft.InvoiceId && !value.IsDeleted,
            ct);
        if (targetInvoice is null ||
            string.IsNullOrWhiteSpace(targetInvoice.TelegramInvoiceChatId) ||
            !targetInvoice.TelegramInvoiceMessageId.HasValue)
        {
            _invoiceCaptionEditDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "پیام PDF این فاکتور برای ویرایش در دسترس نیست.", ct);
            return true;
        }

        var paymentAccounts = await _paymentAccountRepository.GetActiveAsync(ct);
        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>
        {
            new TelegramInlineButton[]
            {
                new("✅ پرداخت‌شده", $"invoicepay:paid:{targetInvoice.Id:N}"),
                new("⏳ در انتظار پرداخت", $"invoicepay:waiting:{targetInvoice.Id:N}")
            }
        };
        foreach (var account in paymentAccounts.Take(4))
        {
            rows.Add(new TelegramInlineButton[]
            {
                new($"📋 کپی شماره کارت {account.BankName}".Trim(), CopyText: account.CardNumber)
            });
        }
        if (string.Equals(targetInvoice.TelegramInvoiceChatId, _options.InvoiceFailureChatId.Trim(), StringComparison.Ordinal))
        {
            rows.Add(new TelegramInlineButton[]
            {
                new("📋 کپی فرمان اتصال گروه", CopyText: $"/connect {targetInvoice.InvoiceNumber}")
            });
        }

        var result = await _sender.EditCaptionWithKeyboardAsync(
            targetInvoice.TelegramInvoiceChatId,
            targetInvoice.TelegramInvoiceMessageId.Value,
            text,
            rows,
            ct);
        if (!result.IsSuccessful)
        {
            await ReplyAsync(message.Chat.Id,
                $"ویرایش کپشن انجام نشد: {result.Error ?? "خطای ناشناخته"}", ct);
            return true;
        }

        targetInvoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _invoiceCaptionEditDrafts.Remove(message.Chat.Id, message.From.Id);
        await ReplyAsync(message.Chat.Id, $"کپشن PDF فاکتور {targetInvoice.InvoiceNumber} ویرایش شد ✅", ct);
        return true;
    }

    private async Task<bool> TryHandleInvoiceResendMessageAsync(
        TelegramMessage message,
        CancellationToken ct)
    {
        if (message.From is null || !_invoiceResendDrafts.IsWaiting(message.Chat.Id, message.From.Id))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _invoiceResendDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "دسترسی مدیریت ندارید.", ct);
            return true;
        }
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(message.Chat.Id, "آیدی مشتری را به‌صورت متن بفرستید.", ct);
            return true;
        }
        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _invoiceResendDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ارسال مجدد فاکتور لغو شد.", ct);
            return true;
        }
        var identity = text.Trim().TrimStart('@').ToLowerInvariant();
        var identityWithAt = $"@{identity}";
        var invoices = await _db.Invoices.AsNoTracking()
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.Order).ThenInclude(value => value!.Items)
                .ThenInclude(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.Customer != null &&
                ((value.Order.Customer.Username != null &&
                  (value.Order.Customer.Username.ToLower() == identity ||
                   value.Order.Customer.Username.ToLower() == identityWithAt)) ||
                 value.Order.Customer.TelegramId == identity))
            .OrderByDescending(value => value.IssuedAt)
            .Take(3)
            .ToArrayAsync(ct);
        if (invoices.Length == 0)
        {
            await ReplyAsync(message.Chat.Id, "برای این آیدی فاکتوری پیدا نشد.", ct);
            return true;
        }
        var buttons = invoices.Select(invoice =>
        {
            var perfumeNames = string.Join("، ", invoice.Order!.Items
                .Where(item => !item.IsDeleted)
                .OrderBy(item => item.RowNumber)
                .Select(item => item.Perfume?.Name ?? item.ManualDescription)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(perfumeNames))
                perfumeNames = "عطر";
            const int maxPerfumeLabelLength = 40;
            if (perfumeNames.Length > maxPerfumeLabelLength)
                perfumeNames = $"{perfumeNames[..(maxPerfumeLabelLength - 1)]}…";

            var invoiceSuffix = invoice.InvoiceNumber.Length <= 4
                ? invoice.InvoiceNumber
                : invoice.InvoiceNumber[^4..];
            var canResend = invoice.TelegramInvoiceChatId != null &&
                            invoice.TelegramInvoiceMessageId != null;

            return (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{perfumeNames} — {invoiceSuffix}" +
                    (canResend ? string.Empty : " ⚠️"),
                    $"invoiceadmin:resend:{invoice.Id:N}")
            };
        }).ToArray();
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
            "یکی از حداکثر سه فاکتور آخر را برای ارسال مجدد انتخاب کنید:", buttons, ct);
        return true;
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
        const string manualGiftPrefix = "invoicebatch:manualgift:";
        if (callback.Data.StartsWith(manualGiftPrefix, StringComparison.Ordinal))
        {
            if (!_manualInvoiceDrafts.TryGet(chatId, userId, out var manualDraft) ||
                manualDraft.Stage != TelegramManualInvoiceStage.AwaitingGiftDecision)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            var choice = callback.Data[manualGiftPrefix.Length..];
            if (choice is not ("yes" or "no"))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct, true);
                return;
            }
            manualDraft.IsGift = choice == "yes";
            manualDraft.Stage = manualDraft.IsGift
                ? TelegramManualInvoiceStage.AwaitingGiftGiver
                : TelegramManualInvoiceStage.AwaitingCustomer;
            _manualInvoiceDrafts.Set(manualDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId, manualDraft.IsGift
                ? "شناسه هدیه‌دهنده را به صورت @username یا Telegram ID وارد کنید:"
                : "شناسه مشتری را به صورت @username یا Telegram ID وارد کنید:", ct);
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
                $"عکس آیتم ۱ از {manualDraft.Lines.Count} را ارسال کنید:\n{manualDraft.Lines[0].Description}", ct);
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
                    manualDraft.ProductPhotoFileIds, userId.ToString(),
                    manualDraft.IsGift ? manualDraft.GiftRecipientIdentity : null, ct);
                _manualInvoiceDrafts.Remove(chatId, userId);
                var paymentTrackingStatus = await SendManualPaymentTrackingReportAsync(
                    result.InvoiceNumbers.Single(), ct);
                var accountingStatus = await SendManualAccountingReportAsync(
                    result.InvoiceNumbers.Single(), ct);
                await ReplyAsync(chatId,
                    $"✅ فاکتور دستی {result.InvoiceNumbers.Single()} صادر شد.\n" +
                    paymentTrackingStatus + "\n" + accountingStatus + "\n" +
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
                var preview = await _invoiceIssuanceService.PreviewCompletedListsAsync(selected.ToArray(), ct);
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش آماده شد.", ct);
                foreach (var completedList in preview.CompletedListMessages)
                {
                    foreach (var part in SplitTelegramMessage(completedList))
                        await ReplyAsync(chatId, part, ct);
                }
                var previewText = "🔎 پیش‌نمایش صدور فاکتور\n" +
                    $"تعداد فاکتور: {preview.InvoiceCount} | جمع: {preview.TotalAmount:N0} تومان\n\n" +
                    string.Join("\n", preview.Lines);
                var previewParts = SplitTelegramMessage(previewText);
                foreach (var part in previewParts.Take(Math.Max(0, previewParts.Count - 1)))
                    await ReplyAsync(chatId, part, ct);
                await _sender.SendInlineKeyboardAsync(chatId.ToString(), previewParts.Last(),
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[] { new TelegramInlineButton("✅ تأیید و ارسال فاکتورها", "invoicebatch:confirmissue") },
                        new[] { new TelegramInlineButton("↩ بازگشت به انتخاب", "invoiceadmin:batch") },
                        new[] { new TelegramInlineButton("❌ لغو", "invoicebatch:cancel") }
                    }, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }
        if (callback.Data == "invoicebatch:confirmissue")
        {
            if (!_invoiceIssuanceDrafts.TryGet(chatId, userId, out var selected) || selected.Count == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "پیش‌نمایش منقضی شده است.", ct);
                return;
            }
            try
            {
                if (await StartInvoiceBottlePriceResolutionAsync(chatId, userId, selected, ct))
                {
                    await _sender.AnswerCallbackAsync(callback.Id, "مبلغ شیشه‌ها لازم است.", ct, true);
                    return;
                }
                await _sender.AnswerCallbackAsync(callback.Id, "صدور فاکتورها شروع شد…", ct);
                var result = await _invoiceIssuanceService.IssueCompletedListsAsync(
                    selected.ToArray(), userId.ToString(), ct);
                _invoiceIssuanceDrafts.Remove(chatId, userId);
                var productionDispatchFailures = await SendProductionCopiesAsync(result.ProductionCopies, ct);
                var paymentTrackingStatus = await SendPaymentTrackingReportAsync(result.BatchId, ct);
                var accountingStatus = await SendAccountingReportsAsync(result.BatchId, ct);
                var productionDispatchStatus = productionDispatchFailures.Count == 0
                    ? $"نسخهٔ چاپ لیبل {result.ProductionCopies.Count} لیست ارسال شد ✅؛ ارسال به صف دکانت پس از ثبت رسیدن عطر انجام می‌شود."
                    : "⚠️ ارسال نسخهٔ عملیاتی کامل نشد:\n" + string.Join("\n", productionDispatchFailures);
                await ReplyAsync(chatId,
                    $"✅ {result.InvoiceCount} فاکتور تجمیعی صادر شد.\n" +
                    $"شماره‌ها: {string.Join("، ", result.InvoiceNumbers)}\n\n" +
                    productionDispatchStatus + "\n" + paymentTrackingStatus + "\n" + accountingStatus + "\n\n" +
                    "ارسال خودکار فاکتور انجام می‌شود؛ موارد بدون گروه یا با خطای دائمی در گروه خطاهای فاکتور ثبت خواهند شد.", ct);
            }
            catch (BottlePriceResolutionRequiredException)
            {
                await StartInvoiceBottlePriceResolutionAsync(chatId, userId, selected, ct);
                await _sender.AnswerCallbackAsync(callback.Id, "مبلغ شیشه لازم است.", ct, true);
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
        var reports = await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(batchId, ct);
        if (reports.Count == 0)
            return "⚠️ گزارش واریز ساخته نشد.";
        var failures = new List<string>();
        foreach (var report in reports)
        {
            var sent = await _sender.SendInlineKeyboardAsync(
                _options.NewPaymentsChatId.Trim(), report.Message,
                BuildPaymentTrackingButtons(report), ct);
            if (!sent.IsSuccessful || !sent.MessageId.HasValue)
            {
                failures.Add(sent.Error ?? "خطای نامشخص");
                continue;
            }
            await _invoiceIssuanceService.SetPaymentTrackingMessageAsync(
                batchId, report.SalesListId, _options.NewPaymentsChatId.Trim(), sent.MessageId.Value, ct);
        }
        return failures.Count == 0
            ? $"گزارش وضعیت {reports.Count} عطر جداگانه به گروه واریز جدید ارسال شد ✅"
            : $"⚠️ ارسال {failures.Count} گزارش واریز ناموفق بود: {string.Join("؛ ", failures)}";
    }

    private async Task<string> SendManualPaymentTrackingReportAsync(
        string invoiceNumber,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.NewPaymentsChatId))
            return "⚠️ گروه واریز جدید تنظیم نشده است.";

        var invoice = await LoadManualInvoiceForPaymentTrackingAsync(invoiceNumber, ct);
        if (invoice is null)
            return "⚠️ گزارش واریز فاکتور دستی ساخته نشد.";

        var sent = await _sender.SendInlineKeyboardAsync(
            _options.NewPaymentsChatId.Trim(),
            FormatManualPaymentTrackingMessage(invoice),
            BuildManualPaymentTrackingButtons(invoice),
            ct);
        if (!sent.IsSuccessful || !sent.MessageId.HasValue)
            return $"⚠️ ارسال گزارش واریز فاکتور دستی ناموفق بود: {sent.Error ?? "خطای نامشخص"}";

        invoice.TelegramPaymentTrackingChatId = _options.NewPaymentsChatId.Trim();
        invoice.TelegramPaymentTrackingMessageId = sent.MessageId.Value;
        invoice.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return "گزارش وضعیت فاکتور دستی به گروه واریز جدید ارسال شد ✅";
    }

    private async Task<string> SendAccountingReportsAsync(Guid batchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AccountingChatId))
            return "⚠️ گروه اطلاع حسابدار تنظیم نشده است.";
        var reports = await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(batchId, ct);
        if (reports.Count == 0)
            return "⚠️ گزارش حسابداری ساخته نشد.";
        var failures = new List<string>();
        foreach (var report in reports)
        {
            var sent = await _sender.SendAsync(
                _options.AccountingChatId.Trim(),
                "🧾 گزارش صدور فاکتور جهت اطلاع حسابدار\n\n" + report.Message,
                ct);
            if (!sent.IsSuccessful)
                failures.Add(sent.Error ?? "خطای نامشخص");
        }
        return failures.Count == 0
            ? $"گزارش {reports.Count} عطر برای حسابدار ارسال شد ✅"
            : $"⚠️ ارسال {failures.Count} گزارش حسابداری ناموفق بود.";
    }

    private async Task<string> SendManualAccountingReportAsync(
        string invoiceNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AccountingChatId))
            return "⚠️ گروه اطلاع حسابدار تنظیم نشده است.";
        var invoice = await LoadManualInvoiceForPaymentTrackingAsync(invoiceNumber, ct);
        if (invoice is null)
            return "⚠️ گزارش حسابداری فاکتور دستی ساخته نشد.";
        var sent = await _sender.SendAsync(
            _options.AccountingChatId.Trim(),
            "🧾 گزارش صدور فاکتور دستی جهت اطلاع حسابدار\n\n" +
            FormatManualPaymentTrackingMessage(invoice), ct);
        return sent.IsSuccessful
            ? "گزارش فاکتور دستی برای حسابدار ارسال شد ✅"
            : $"⚠️ ارسال گزارش حسابداری فاکتور دستی ناموفق بود: {sent.Error ?? "خطای نامشخص"}";
    }

    private async Task RefreshManualPaymentTrackingReportAsync(
        Guid invoiceId,
        long sourceChatId,
        CancellationToken ct)
    {
        var invoice = await LoadManualInvoiceForPaymentTrackingAsync(invoiceId, ct);
        if (invoice is null ||
            string.IsNullOrWhiteSpace(invoice.TelegramPaymentTrackingChatId) ||
            !invoice.TelegramPaymentTrackingMessageId.HasValue)
            return;

        var refreshed = await _sender.EditTextWithKeyboardAsync(
            invoice.TelegramPaymentTrackingChatId,
            invoice.TelegramPaymentTrackingMessageId.Value,
            FormatManualPaymentTrackingMessage(invoice),
            BuildManualPaymentTrackingButtons(invoice),
            ct);
        if (!refreshed.IsSuccessful && !IsTelegramMessageUnchanged(refreshed.Error))
            await ReplyAsync(sourceChatId,
                $"⚠️ وضعیت مالی ثبت شد اما گزارش فاکتور دستی بروزرسانی نشد: {refreshed.Error}", ct);
    }

    private static bool IsTelegramMessageUnchanged(string? error) =>
        error?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;

    private Task<Invoice?> LoadManualInvoiceForPaymentTrackingAsync(
        string invoiceNumber,
        CancellationToken ct) => _db.Invoices
        .Include(value => value.Order)
            .ThenInclude(value => value!.Customer)
        .Include(value => value.Order)
            .ThenInclude(value => value!.Items)
        .FirstOrDefaultAsync(value => value.InvoiceNumber == invoiceNumber && !value.IsDeleted &&
            value.Order != null && value.Order.Source == OrderSource.ManualInvoice, ct);

    private Task<Invoice?> LoadManualInvoiceForPaymentTrackingAsync(
        Guid invoiceId,
        CancellationToken ct) => _db.Invoices
        .Include(value => value.Order)
            .ThenInclude(value => value!.Customer)
        .Include(value => value.Order)
            .ThenInclude(value => value!.Items)
        .FirstOrDefaultAsync(value => value.Id == invoiceId && !value.IsDeleted &&
            value.Order != null && value.Order.Source == OrderSource.ManualInvoice, ct);

    private static string FormatManualPaymentTrackingMessage(Invoice invoice)
    {
        var order = invoice.Order!;
        var customer = order.Customer;
        var identity = !string.IsNullOrWhiteSpace(customer?.Username)
            ? $"@{customer.Username.TrimStart('@')}"
            : customer?.TelegramId ?? customer?.FullName ?? "مشتری نامشخص";
        var status = invoice.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Paid ||
                     order.Status == OrderStatus.Paid
            ? "✅ پرداخت‌شده"
            : "🔴 در انتظار پرداخت";
        var items = order.Items.Where(item => !item.IsDeleted)
            .Select(item => $"• {item.ManualDescription ?? "آیتم دستی"} — {item.RequestedVolumeMl} میل")
            .ToArray();
        return $"💳 واریز جدید — فاکتور دستی\n" +
               $"مشتری: {identity}\n" +
               $"فاکتور: {invoice.InvoiceNumber}\n" +
               $"مبلغ: {invoice.TotalAmount:N0} تومان\n" +
               $"وضعیت: {status}\n\n" +
               string.Join("\n", items) +
               $"\n\nآخرین بروزرسانی: {DateTime.UtcNow.AddHours(3.5):yyyy/MM/dd HH:mm}";
    }

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> BuildManualPaymentTrackingButtons(
        Invoice invoice) => invoice.Status == ZibasheERP.Domain.Enums.InvoiceStatus.Paid ||
                            invoice.Order?.Status == OrderStatus.Paid
        ? new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[] { new TelegramInlineButton("✅ پرداخت‌شده", $"invoicepay:paid:{invoice.Id:N}") }
        }
        : new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[]
            {
                new TelegramInlineButton("✅ پرداخت‌شده", $"invoicepay:paid:{invoice.Id:N}"),
                new TelegramInlineButton("⏳ در انتظار پرداخت", $"invoicepay:waiting:{invoice.Id:N}")
            }
        };

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
            draft.ProductPhotoFileIds.Add(photo.FileId);
            if (draft.ProductPhotoFileIds.Count < draft.Lines.Count)
            {
                var nextIndex = draft.ProductPhotoFileIds.Count;
                _manualInvoiceDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id,
                    $"عکس آیتم {nextIndex + 1} از {draft.Lines.Count} را ارسال کنید:\n{draft.Lines[nextIndex].Description}", ct);
                return true;
            }
            draft.Stage = TelegramManualInvoiceStage.AwaitingConfirmation;
            _manualInvoiceDrafts.Set(draft);
            var total = draft.Lines.Sum(line => line.Quantity * line.UnitAmount + line.BottleAmount);
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                $"پیش‌نمایش فاکتور دستی\n" +
                (draft.IsGift
                    ? $"هدیه‌دهنده: {draft.CustomerIdentity}\nهدیه‌گیرنده: {draft.GiftRecipientIdentity}\n"
                    : $"مشتری: {draft.CustomerIdentity}\n") +
                $"تعداد ردیف: {draft.Lines.Count}\nمبلغ کل: {total:N0} تومان\nعکس همه آیتم‌ها: دریافت شد ✅",
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
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingGiftGiver)
        {
            draft.CustomerIdentity = text;
            draft.Stage = TelegramManualInvoiceStage.AwaitingGiftRecipient;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id, "شناسه هدیه‌گیرنده را به صورت @username یا Telegram ID وارد کنید:", ct);
            return true;
        }
        if (draft.Stage == TelegramManualInvoiceStage.AwaitingGiftRecipient)
        {
            draft.GiftRecipientIdentity = text;
            draft.Stage = TelegramManualInvoiceStage.AwaitingLine;
            _manualInvoiceDrafts.Set(draft);
            await ReplyAsync(message.Chat.Id,
                "نام یا شرح آیتم اول را وارد کنید:\n\nفرمت سریع: عطر تست / 5 / 250000 / 30000", ct);
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
                    $"عکس آیتم ۱ از {draft.Lines.Count} را ارسال کنید:\n{draft.Lines[0].Description}", ct);
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
                    $"عکس آیتم ۱ از {draft.Lines.Count} را ارسال کنید:\n{draft.Lines[0].Description}", ct);
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

    private async Task<bool> TryHandleInvoiceBottlePriceResolutionMessageAsync(
        TelegramMessage message, CancellationToken ct)
    {
        if (!_invoiceBottlePriceResolutionDrafts.TryGet(message.Chat.Id, message.From!.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id, ct))
        {
            _invoiceBottlePriceResolutionDrafts.Remove(message.Chat.Id, message.From.Id);
            return true;
        }
        var priceLines = (message.Text ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (priceLines.Length != draft.Items.Count)
        {
            await ReplyAsync(message.Chat.Id,
                $"تعداد قیمت‌ها باید دقیقاً {draft.Items.Count} خط باشد؛ هر خط برای مورد هم‌شماره.", ct);
            return true;
        }
        var prices = new decimal[priceLines.Length];
        for (var index = 0; index < priceLines.Length; index++)
        {
            if (!TryParseNonNegativeDecimal(priceLines[index], out prices[index]) || prices[index] <= 0)
            {
                await ReplyAsync(message.Chat.Id,
                    $"قیمت خط {index + 1} معتبر نیست؛ مبلغی بزرگ‌تر از صفر و به تومان وارد کنید.", ct);
                return true;
            }
        }

        var itemArray = draft.Items.ToArray();
        var requestIds = itemArray.Select(value => value.SalesListRequestId).ToArray();
        var requests = await _db.SalesListRequests.Where(value =>
            requestIds.Contains(value.Id) && !value.IsDeleted).ToArrayAsync(ct);
        if (requests.Length != requestIds.Length)
        {
            _invoiceBottlePriceResolutionDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "حداقل یکی از درخواست‌ها دیگر پیدا نشد؛ لیست‌ها را دوباره انتخاب کنید.", ct);
            return true;
        }

        var priceByRequestId = itemArray.Select((item, index) => (item.SalesListRequestId, Price: prices[index]))
            .ToDictionary(value => value.SalesListRequestId, value => value.Price);
        foreach (var request in requests)
        {
            request.BottlePrice = priceByRequestId[request.Id];
            request.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        _invoiceBottlePriceResolutionDrafts.Remove(message.Chat.Id, message.From.Id);
        await ReplyAsync(message.Chat.Id,
            $"✅ قیمت {prices.Length} شیشه ثبت شد. صدور فاکتورها ادامه پیدا کرد…", ct);

        try
        {
            var result = await _invoiceIssuanceService.IssueCompletedListsAsync(
                draft.SelectedSalesListIds.ToArray(), message.From.Id.ToString(), ct);
            _invoiceIssuanceDrafts.Remove(message.Chat.Id, message.From.Id);
            var productionFailures = await SendProductionCopiesAsync(result.ProductionCopies, ct);
            var paymentTrackingStatus = await SendPaymentTrackingReportAsync(result.BatchId, ct);
            var accountingStatus = await SendAccountingReportsAsync(result.BatchId, ct);
            var productionStatus = productionFailures.Count == 0
                ? $"نسخهٔ چاپ لیبل {result.ProductionCopies.Count} لیست ارسال شد ✅؛ صف دکانت بعد از ثبت رسیدن عطر ساخته می‌شود."
                : "⚠️ ارسال نسخهٔ عملیاتی کامل نشد:\n" + string.Join("\n", productionFailures);
            await ReplyAsync(message.Chat.Id,
                $"✅ {result.InvoiceCount} فاکتور تجمیعی صادر شد.\n" +
                $"شماره‌ها: {string.Join("، ", result.InvoiceNumbers)}\n\n" +
                productionStatus + "\n" + paymentTrackingStatus + "\n" + accountingStatus, ct);
        }
        catch (BottlePriceResolutionRequiredException)
        {
            await StartInvoiceBottlePriceResolutionAsync(
                message.Chat.Id, message.From.Id, draft.SelectedSalesListIds, ct);
        }
        catch (InvalidOperationException exception)
        {
            await ReplyAsync(message.Chat.Id, $"⚠️ صدور فاکتور انجام نشد:\n{exception.Message}", ct);
            await SendInvoiceBatchSelectionAsync(message.Chat.Id, message.From.Id, ct);
        }
        return true;
    }

    private async Task<bool> StartInvoiceBottlePriceResolutionAsync(
        long chatId,
        long userId,
        IReadOnlyCollection<Guid> selectedSalesListIds,
        CancellationToken ct)
    {
        var listIds = selectedSalesListIds.Distinct().ToArray();
        var items = await _db.SalesListRequests.AsNoTracking()
            .Where(request => !request.IsDeleted && listIds.Contains(request.SalesListId) &&
                request.Kind == SalesListRequestKind.CurrentBottle &&
                request.Status == SalesListRequestStatus.Confirmed &&
                !request.IsBottleOwner && !request.IsComplimentaryBottle &&
                request.BottlePrice <= 0 &&
                (request.Bottle == null || request.Bottle.SalePrice <= 0))
            .OrderBy(request => request.SalesList.PublicCode)
            .ThenBy(request => request.ConfirmedAt ?? request.CreatedAt)
            .Select(request => new TelegramInvoiceBottlePriceResolutionItem(
                request.Id,
                request.SalesList.PublicCode,
                string.IsNullOrWhiteSpace(request.TelegramUsername)
                    ? request.TelegramUserId
                    : "@" + request.TelegramUsername,
                request.Bottle == null ? "شیشه نامشخص" : request.Bottle.Name))
            .ToArrayAsync(ct);
        if (items.Length == 0)
            return false;

        _invoiceBottlePriceResolutionDrafts.Set(chatId, userId,
            new TelegramInvoiceBottlePriceResolutionDraft(items, listIds));
        var lines = items.Select((item, index) =>
            $"{index + 1}. لیست {item.SalesListPublicCode} | {item.CustomerIdentity} | {item.BottleName}").ToArray();
        var chunk = new System.Text.StringBuilder($"⚠️ قیمت {items.Length} شیشه مشخص نیست:\n\n");
        foreach (var line in lines)
        {
            if (chunk.Length + line.Length + 1 > 3500)
            {
                await ReplyAsync(chatId, chunk.ToString().TrimEnd(), ct);
                chunk.Clear();
                chunk.Append("ادامه موارد:\n\n");
            }
            chunk.AppendLine(line);
        }
        if (chunk.Length > 0)
            await ReplyAsync(chatId, chunk.ToString().TrimEnd(), ct);
        await ReplyAsync(chatId,
            $"قیمت‌ها را دقیقاً در {items.Length} خط و به ترتیب شماره‌های بالا وارد کنید؛ هر خط فقط مبلغ تومان.\n" +
            "مثال:\n30000\n35000", ct);
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

    private async Task<bool> IsAuthorizedInvoiceAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        if (IsPrimaryOwner(userId))
            return true;

        var isPrivateChat = chatId == userId;
        if (AuthorizedAdminUntil.TryGetValue(userId, out var expiresAt) &&
            expiresAt > DateTime.UtcNow)
            return true;

        var hasAdminChat = long.TryParse(_options.AdminChatId, out var adminChatId);
        if (isPrivateChat && !hasAdminChat)
            return false;

        var authorizationChatId = isPrivateChat ? adminChatId : chatId;
        var isAdministrator = await _sender.IsChatAdministratorAsync(
            authorizationChatId.ToString(), userId.ToString(), ct);
        if (isAdministrator)
            AuthorizedAdminUntil[userId] = DateTime.UtcNow.AddMinutes(3);
        else
            AuthorizedAdminUntil.TryRemove(userId, out _);
        return isAdministrator;
    }

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
            var perfumes = await _perfumeRepository.GetAllActiveForPriceUpdateAsync(ct);
            var samples = perfumes.Take(3).Select(value =>
                $"{value.EnglishName}: {value.PricePerMl:N0} ← {AdjustedPrice(value.PricePerMl, draft.Value):N0}");
            var openListsCount = await _salesListRepository.CountAllOpenAsync(ct);
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
            ClearAdminWorkflowDrafts(chatId, userId);
            var kind = parts[2] switch
            {
                "next" => TelegramAdminRequestKind.NextBottle,
                "edit" => TelegramAdminRequestKind.EditList,
                "cleanup" => TelegramAdminRequestKind.CleanupList,
                "queue" => TelegramAdminRequestKind.ManageBottleQueue,
                "gift" => TelegramAdminRequestKind.GiftRequest,
                "changevolume" => TelegramAdminRequestKind.ChangeRequestVolume,
                "removeitem" => TelegramAdminRequestKind.RemoveSingleRequest,
                "removemultiple" => TelegramAdminRequestKind.RemoveMultipleCustomerRequests,
                "labelnoid" => TelegramAdminRequestKind.OmitRequestIdentityOnLabel,
                "labeltext" => TelegramAdminRequestKind.SetRequestLabelIdentityText,
                "removeall" => TelegramAdminRequestKind.RemoveCustomerRequests,
                _ => TelegramAdminRequestKind.CustomRequest
            };
            _adminRequestDrafts.Set(new TelegramAdminRequestDraft
            {
                ChatId = chatId, UserId = userId, Kind = kind,
                Stage = kind is TelegramAdminRequestKind.RemoveCustomerRequests or
                    TelegramAdminRequestKind.RemoveMultipleCustomerRequests
                    ? TelegramAdminRequestStage.AwaitingIdentity
                    : TelegramAdminRequestStage.AwaitingListSearch
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (kind is TelegramAdminRequestKind.RemoveCustomerRequests or
                TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
            {
                await ReplyAsync(chatId,
                    kind == TelegramAdminRequestKind.RemoveMultipleCustomerRequests
                        ? "آیدی مشتری را به‌صورت @username یا Telegram ID وارد کنید.\n" +
                          "تمام آیتم‌های فعال او، شامل لیست و صف باتل، برای تیک‌زدن نمایش داده می‌شود."
                        : "آیدی مشتری را به‌صورت @username یا Telegram ID وارد کنید.\n" +
                          "همه آیتم‌های فعال او در تمام لیست‌های باز نمایش داده می‌شود تا پیش از حذف تأیید کنید.", ct);
                return;
            }
            await ReplyAsync(chatId,
                "کد لیست یا بخشی از نام فارسی/انگلیسی عطر را وارد کنید:", ct);
            return;
        }

        if (parts.Length == 4 && parts[1] == "list" && Guid.TryParseExact(parts[3], "N", out var listId))
        {
            var list = await _salesListRepository.GetByIdAsync(listId, ct);
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
            await ReplyAsync(chatId,
                parts[2] == "photo" ? "عکس جدید عطر را ارسال کنید:"
                : parts[2] == "perfumenotes" ? CombinedNotesPrompt
                : parts[2] == "pricing" ? SalesListPricingPrompt
                : "مقدار جدید را وارد کنید:", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "changeitem" &&
            Guid.TryParseExact(parts[2], "N", out var changedRequestId))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var changeDraft) ||
                changeDraft.Kind != TelegramAdminRequestKind.ChangeRequestVolume)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            var request = await _salesListRequestRepository.GetAsync(changedRequestId, ct);
            if (request is null || request.SalesListId != changeDraft.SalesListId ||
                request.Status != SalesListRequestStatus.Confirmed ||
                request.Kind != SalesListRequestKind.CurrentBottle)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم فعال پیدا نشد.", ct, true);
                return;
            }
            changeDraft.SelectedRequestId = request.Id;
            changeDraft.OriginalVolumeMl = request.VolumeMl;
            changeDraft.Stage = TelegramAdminRequestStage.AwaitingVolume;
            _adminRequestDrafts.Set(changeDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId,
                $"مقدار جدید برای {DisplayUser(request)} را به میل وارد کنید. مقدار فعلی: {request.VolumeMl} میل", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "changebottle")
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var changeDraft) ||
                changeDraft.Kind != TelegramAdminRequestKind.ChangeRequestVolume ||
                changeDraft.Stage != TelegramAdminRequestStage.AwaitingBottleType)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            if (parts[2] is not ("normal" or "fancy"))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "نوع شیشه نامعتبر است.", ct, true);
                return;
            }
            changeDraft.BottleType = parts[2] == "fancy" ? BottleType.Fancy : BottleType.Normal;
            changeDraft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
            _adminRequestDrafts.Set(changeDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendChangeRequestVolumeConfirmationAsync(changeDraft, ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "removeitem" &&
            Guid.TryParseExact(parts[2], "N", out var removableRequestId))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var removalDraft) ||
                removalDraft.Kind != TelegramAdminRequestKind.RemoveSingleRequest)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            var removable = await _salesListRequestRepository.GetAsync(removableRequestId, ct);
            if (removable is null || removable.SalesListId != removalDraft.SalesListId)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم فعال پیدا نشد.", ct, true);
                return;
            }
            try
            {
                await _salesListRequestRepository.RemoveConfirmedAsync(removableRequestId, ct);
                await RefreshChannelSalesListAsync(removable.SalesListId, ct);
                var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(auditChatId,
                    $"🗑 حذف یک آیتم\nثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
                    $"لیست: {removalDraft.PublicCode} — {removalDraft.SalesListName}\n" +
                    $"مشتری: {DisplayUser(removable)}\nمقدار: {removable.VolumeMl} میل\n" +
                    $"درخواست: {removableRequestId:N}", ct);
                _adminRequestDrafts.Remove(chatId, userId);
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم حذف شد ✅", ct);
                await ReplyAsync(chatId, "آیتم حذف و پست کانال به‌روزرسانی شد ✅", ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }

        if (parts.Length == 3 && parts[1] == "multirem" &&
            Guid.TryParseExact(parts[2], "N", out var multiRemovalRequestId))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var multiRemovalDraft) ||
                multiRemovalDraft.Kind != TelegramAdminRequestKind.RemoveMultipleCustomerRequests ||
                multiRemovalDraft.Stage != TelegramAdminRequestStage.AwaitingConfirmation)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            if (!multiRemovalDraft.AvailableRequestIds.Contains(multiRemovalRequestId))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این آیتم در فهرست مشتری نیست.", ct, true);
                return;
            }

            if (!multiRemovalDraft.SelectedRequestIds.Add(multiRemovalRequestId))
                multiRemovalDraft.SelectedRequestIds.Remove(multiRemovalRequestId);
            _adminRequestDrafts.Set(multiRemovalDraft);
            await _sender.AnswerCallbackAsync(callback.Id,
                multiRemovalDraft.SelectedRequestIds.Contains(multiRemovalRequestId) ? "انتخاب شد ☑️" : "از انتخاب خارج شد.", ct);
            await UpdateMultipleRemovalSelectionAsync(callback.Message!, multiRemovalDraft, ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "multirempage" &&
            int.TryParse(parts[2], out var requestedMultipleRemovalPage))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var multiRemovalDraft) ||
                multiRemovalDraft.Kind != TelegramAdminRequestKind.RemoveMultipleCustomerRequests ||
                multiRemovalDraft.Stage != TelegramAdminRequestStage.AwaitingConfirmation)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            multiRemovalDraft.MultipleRemovalPage = Math.Max(0, requestedMultipleRemovalPage);
            _adminRequestDrafts.Set(multiRemovalDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await UpdateMultipleRemovalSelectionAsync(callback.Message!, multiRemovalDraft, ct);
            return;
        }

        if (callback.Data == "adminrequest:multiremconfirm")
        {
            await ConfirmAdminRequestAsync(callback, ct);
            return;
        }

        if (callback.Data == "adminrequest:multiremfinish")
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var multiRemovalDraft) ||
                multiRemovalDraft.Kind != TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            _adminRequestDrafts.Remove(chatId, userId);
            await _sender.AnswerCallbackAsync(callback.Id, "عملیات پایان یافت ✅", ct);
            await _sender.EditTextAsync(chatId.ToString(), callback.Message!.MessageId,
                "عملیات حذف چند آیتم پایان یافت ✅", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "labelnoid" &&
            Guid.TryParseExact(parts[2], "N", out var labelRequestId))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var labelDraft) ||
                labelDraft.Kind != TelegramAdminRequestKind.OmitRequestIdentityOnLabel)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            try
            {
                var changed = await _salesListRequestRepository.GetAsync(labelRequestId, ct);
                if (changed is null || changed.SalesListId != labelDraft.SalesListId)
                    throw new InvalidOperationException("آیتم فعال پیدا نشد.");
                await _salesListRequestRepository.SetOmitIdentityOnLabelAsync(labelRequestId, ct);
                changed.OmitIdentityOnLabel = true;
                await RefreshChannelSalesListAsync(changed.SalesListId, ct, includeCompletedRequests: true);
                var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(auditChatId,
                    $"🏷 حذف آیدی از لیبل آیتم\nثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
                    $"لیست: {labelDraft.PublicCode} — {labelDraft.SalesListName}\n" +
                    $"مشتری: {DisplayUser(changed)}\nمقدار: {changed.VolumeMl} میل\n" +
                    $"درخواست: {labelRequestId:N}", ct);
                _adminRequestDrafts.Remove(chatId, userId);
                await _sender.AnswerCallbackAsync(callback.Id, "لیبل بدون آیدی ثبت شد ✅", ct);
                await ReplyAsync(chatId,
                    "درخواست حذف آیدی از لیبل ثبت و نشان B Id به آیتم اضافه شد ✅", ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }

        if (parts.Length == 3 && parts[1] == "labeltext" &&
            Guid.TryParseExact(parts[2], "N", out var customLabelRequestId))
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var labelDraft) ||
                labelDraft.Kind != TelegramAdminRequestKind.SetRequestLabelIdentityText)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            var request = await _salesListRequestRepository.GetAsync(customLabelRequestId, ct);
            if (request is null || request.SalesListId != labelDraft.SalesListId)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم فعال پیدا نشد.", ct, true);
                return;
            }
            labelDraft.SelectedRequestId = customLabelRequestId;
            labelDraft.Stage = TelegramAdminRequestStage.AwaitingLabelIdentityText;
            _adminRequestDrafts.Set(labelDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId,
                "متن دلخواه روی لیبل را وارد کنید؛ مثال: ماه\nحداکثر ۸۰ نویسه.", ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "queue" && parts[2] == "reorder")
        {
            if (!_adminRequestDrafts.TryGet(chatId, userId, out var queueDraft) ||
                queueDraft.Kind != TelegramAdminRequestKind.ManageBottleQueue)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct);
                return;
            }
            var requests = await _salesListRequestRepository.GetConfirmedAsync(queueDraft.SalesListId, ct);
            var queue = requests.Where(request => request.Kind == SalesListRequestKind.NextBottle).ToArray();
            if (queue.Length == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "صف باتل خالی است.", ct, true);
                return;
            }
            queueDraft.QueueRequestIds.Clear();
            queueDraft.QueueRequestIds.AddRange(queue.Select(request => request.Id));
            queueDraft.Stage = TelegramAdminRequestStage.AwaitingQueueOrder;
            _adminRequestDrafts.Set(queueDraft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendQueueReorderPromptAsync(queueDraft, queue, ct);
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
                    await RefreshChannelSalesListAsync(changed.SalesListId, ct, includeCompletedRequests: true);
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
            if (parts[2] is not ("normal" or "fancy" or "owner" or "free"))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "نوع شیشه نامعتبر است.", ct, true);
                return;
            }
            draft.IsBottleOwner = parts[2] == "owner";
            draft.IsComplimentaryBottle = parts[2] == "free";
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

    private void ClearAdminWorkflowDrafts(long chatId, long userId)
    {
        _adminSalesListDrafts.Remove(chatId, userId);
        _ownerPricingDrafts.Remove(chatId, userId);
        _adminRequestDrafts.Remove(chatId, userId);
        _invoiceIssuanceDrafts.Remove(chatId, userId);
        _invoiceCaptionEditDrafts.Remove(chatId, userId);
        _manualInvoiceDrafts.Remove(chatId, userId);
        _invoiceStickerDrafts.Remove(chatId, userId);
        _invoiceInventoryDrafts.Remove(chatId, userId);
        _decantPhotoDrafts.Remove(chatId, userId);
        ImportEditDrafts.TryRemove((chatId, userId), out _);
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
            var publicCode = int.TryParse(normalizedCode, out var parsedPublicCode)
                ? parsedPublicCode
                : (int?)null;
            var lists = (await _salesListRepository.SearchForAdminAsync(query, publicCode, 10, ct))
                .Where(x => draft.Kind is TelegramAdminRequestKind.OmitRequestIdentityOnLabel or
                    TelegramAdminRequestKind.SetRequestLabelIdentityText
                    ? x.Status != SalesListStatus.Cancelled
                    : x.Status is SalesListStatus.Open or SalesListStatus.Full)
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
                TelegramAdminRequestKind.ChangeRequestVolume => "v",
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
            if (draft.EditField == "perfumenotes" &&
                !TryParseCombinedNotes(input, out _, out _, out _))
            {
                await ReplyAsync(message.Chat.Id,
                    "فرمت نت‌ها معتبر نیست. یک خط برای تک‌نت یا دقیقاً سه خط برای نت ابتدایی، میانی و پایانی بفرستید.", ct);
                return true;
            }
            if (draft.EditField == "pricing" &&
                !TryParseSalesListPricing(input, out _, out _, out _, out var pricingError))
            {
                await ReplyAsync(message.Chat.Id, $"{pricingError}\n\n{SalesListPricingPrompt}", ct);
                return true;
            }
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
        if (draft.Stage == TelegramAdminRequestStage.AwaitingQueueOrder)
        {
            var lines = input.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                await ReplyAsync(message.Chat.Id, "صف جدید خالی است؛ حداقل یک آیدی وارد کنید.", ct);
                return true;
            }
            var current = await _db.SalesListRequests.Where(request =>
                    request.SalesListId == draft.SalesListId && !request.IsDeleted &&
                    request.Kind == SalesListRequestKind.NextBottle &&
                    request.Status == SalesListRequestStatus.Confirmed)
                .OrderBy(request => request.ConfirmedAt).ThenBy(request => request.CreatedAt).ThenBy(request => request.Id)
                .ToArrayAsync(ct);
            if (current.Length != draft.QueueRequestIds.Count ||
                current.Select(request => request.Id).Except(draft.QueueRequestIds).Any())
            {
                await ReplyAsync(message.Chat.Id,
                    "صف باتل هم‌زمان تغییر کرده است؛ عملیات لغو شد. دوباره از منوی مدیریت صف وارد شوید.", ct);
                _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                return true;
            }
            var remaining = current.ToList();
            var ordered = new List<SalesListRequest>(lines.Length);
            var now = DateTime.UtcNow;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryParseQueueOrderLine(lines[index], out var identity, out var explicitVolume, out var lineError))
                {
                    await ReplyAsync(message.Chat.Id, $"خط {index + 1}: {lineError}", ct);
                    return true;
                }
                var expected = CanonicalQueueIdentity(identity);
                var match = remaining.FirstOrDefault(request =>
                    CanonicalQueueIdentity(QueueOrderIdentity(request)) == expected);
                if (match is not null)
                {
                    if (explicitVolume.HasValue)
                        match.VolumeMl = explicitVolume.Value;
                    ordered.Add(match);
                    remaining.Remove(match);
                    continue;
                }

                if (identity.Contains(" for ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync(message.Chat.Id,
                        $"خط {index + 1}: هدیه جدید را از مسیر «ثبت هدیه» اضافه کنید؛ در این بخش فقط هدیه موجود قابل جابه‌جایی است.", ct);
                    return true;
                }
                var normalizedUsername = NormalizeAdminRequestUsername(identity);
                var telegramId = normalizedUsername is null
                    ? new string(identity.Where(char.IsDigit).ToArray())
                    : $"admin-username:{normalizedUsername.ToLowerInvariant()}";
                if (normalizedUsername is null && telegramId.Length < 5)
                {
                    await ReplyAsync(message.Chat.Id,
                        $"خط {index + 1}: آیدی معتبر نیست؛ @username یا Telegram ID وارد کنید.", ct);
                    return true;
                }
                ordered.Add(new SalesListRequest
                {
                    Id = Guid.NewGuid(), CreatedAt = now, SalesListId = draft.SalesListId,
                    TelegramUsername = normalizedUsername, TelegramUserId = telegramId,
                    VolumeMl = explicitVolume ?? 30, PerfumePricePerMl = 0,
                    Kind = SalesListRequestKind.NextBottle, Status = SalesListRequestStatus.Confirmed,
                    CreatedByAdmin = true, ExpiresAt = DateTime.MaxValue,
                    ExternalReference = $"admin-queue-replace:{Guid.NewGuid():N}"
                });
            }
            try
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                foreach (var removed in remaining)
                {
                    removed.Status = SalesListRequestStatus.Cancelled;
                    removed.IsDeleted = true;
                    removed.UpdatedAt = now;
                }
                var list = await _db.SalesLists.FirstOrDefaultAsync(value =>
                    value.Id == draft.SalesListId && !value.IsDeleted, ct)
                    ?? throw new InvalidOperationException("لیست پیدا نشد.");
                for (var index = 0; index < ordered.Count; index++)
                {
                    var request = ordered[index];
                    request.PerfumePricePerMl = list.PricePerMl;
                    request.ConfirmedAt = now.AddTicks(index);
                    request.UpdatedAt = now;
                    if (_db.Entry(request).State == EntityState.Detached)
                        await _db.SalesListRequests.AddAsync(request, ct);
                }
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                await RefreshChannelSalesListAsync(draft.SalesListId, ct, includeCompletedRequests: true);
                var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(auditChatId,
                    $"↕️ ویرایش یکجای ترتیب صف باتل\nثبت‌کننده: {DisplayTelegramUser(message.From)}\n" +
                    $"لیست: {draft.PublicCode} — {draft.SalesListName}\n" +
                    $"تعداد قبلی: {current.Length} | تعداد جدید: {ordered.Count} | حذف: {remaining.Count} | اضافه: {ordered.Count(value => value.CreatedAt == now)}", ct);
                _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                await ReplyAsync(message.Chat.Id, "ترتیب صف باتل ذخیره و پست کانال به‌روزرسانی شد ✅", ct);
            }
            catch (InvalidOperationException exception)
            {
                await ReplyAsync(message.Chat.Id, exception.Message, ct);
            }
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingLabelIdentityText)
        {
            try
            {
                await _salesListRequestRepository.SetLabelIdentityTextAsync(
                    draft.SelectedRequestId, input, ct);
                var changed = await _salesListRequestRepository.GetAsync(draft.SelectedRequestId, ct);
                if (changed is null || changed.SalesListId != draft.SalesListId)
                    throw new InvalidOperationException("آیتم فعال پیدا نشد.");
                changed.LabelIdentityText = input.Trim();
                changed.OmitIdentityOnLabel = false;
                await RefreshChannelSalesListAsync(changed.SalesListId, ct);
                var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(auditChatId,
                    $"✏️ ثبت نام دلخواه روی لیبل\nثبت‌کننده: {DisplayTelegramUser(message.From)}\n" +
                    $"لیست: {draft.PublicCode} — {draft.SalesListName}\n" +
                    $"مشتری/هدیه‌گیرنده: {DisplayUser(changed)}\nمتن لیبل: {input.Trim()}\n" +
                    $"درخواست: {draft.SelectedRequestId:N}", ct);
                _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                await ReplyAsync(message.Chat.Id,
                    $"نام «{input.Trim()}» برای لیبل ثبت و لیست به‌روزرسانی شد ✅", ct);
            }
            catch (InvalidOperationException exception)
            {
                await ReplyAsync(message.Chat.Id, exception.Message, ct);
            }
            return true;
        }
        if (draft.Stage == TelegramAdminRequestStage.AwaitingIdentity)
        {
            if (draft.Kind is TelegramAdminRequestKind.RemoveSingleRequest or
                TelegramAdminRequestKind.ChangeRequestVolume or
                TelegramAdminRequestKind.OmitRequestIdentityOnLabel or
                TelegramAdminRequestKind.SetRequestLabelIdentityText)
            {
                var identity = input.Trim();
                var username = NormalizeAdminRequestUsername(identity);
                var telegramId = username is null
                    ? new string(identity.Where(char.IsDigit).ToArray())
                    : string.Empty;
                if (username is null && telegramId.Length < 5)
                {
                    await ReplyAsync(message.Chat.Id, "شناسه نامعتبر است؛ @username یا Telegram ID وارد کنید.", ct);
                    return true;
                }
                var requests = draft.Kind is TelegramAdminRequestKind.OmitRequestIdentityOnLabel or
                    TelegramAdminRequestKind.SetRequestLabelIdentityText
                    ? await _salesListRequestRepository.GetForLabelAdministrationAsync(draft.SalesListId, ct)
                    : await _salesListRequestRepository.GetConfirmedAsync(draft.SalesListId, ct);
                var matches = requests
                    .Where(request => draft.Kind != TelegramAdminRequestKind.ChangeRequestVolume ||
                        request.Kind == SalesListRequestKind.CurrentBottle)
                    .Where(request =>
                        ((username is not null && !string.IsNullOrWhiteSpace(request.TelegramUsername) &&
                          string.Equals(NormalizeAdminRequestUsername(request.TelegramUsername), username,
                              StringComparison.OrdinalIgnoreCase)) ||
                         (!string.IsNullOrWhiteSpace(telegramId) &&
                          string.Equals(request.TelegramUserId, telegramId, StringComparison.Ordinal)) ||
                         (username is not null && !string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername) &&
                          string.Equals(NormalizeAdminRequestUsername(request.GiftRecipientTelegramUsername), username,
                              StringComparison.OrdinalIgnoreCase)) ||
                         (!string.IsNullOrWhiteSpace(telegramId) &&
                          string.Equals(request.GiftRecipientTelegramUserId, telegramId,
                              StringComparison.Ordinal))))
                    .OrderBy(request => request.CreatedAt)
                    .ToArray();
                var showingAllActiveItems = false;
                if (matches.Length == 0 &&
                    draft.Kind == TelegramAdminRequestKind.RemoveSingleRequest &&
                    requests.Count > 0)
                {
                    matches = requests
                        .OrderBy(request => request.CreatedAt)
                        .ToArray();
                    showingAllActiveItems = true;
                }
                if (matches.Length == 0)
                {
                    await ReplyAsync(message.Chat.Id,
                        "آیتم فعالی برای این مشتری در لیست انتخاب‌شده پیدا نشد.", ct);
                    return true;
                }
                draft.Identity = identity;
                _adminRequestDrafts.Set(draft);
                var rows = matches.Select(request =>
                    (IReadOnlyCollection<TelegramInlineButton>)new[]
                    {
                        new TelegramInlineButton(
                            $"{(draft.Kind == TelegramAdminRequestKind.RemoveSingleRequest ? "🗑" : draft.Kind == TelegramAdminRequestKind.ChangeRequestVolume ? "↕️" : draft.Kind == TelegramAdminRequestKind.OmitRequestIdentityOnLabel ? "🏷" : "✏️")} " +
                            $"{DisplayUser(request)}{(request.IsGift ? $" برای @{request.GiftRecipientTelegramUsername ?? request.GiftRecipientTelegramUserId}" : string.Empty)} — {request.VolumeMl} میل",
                            $"adminrequest:{(draft.Kind == TelegramAdminRequestKind.RemoveSingleRequest ? "removeitem" : draft.Kind == TelegramAdminRequestKind.ChangeRequestVolume ? "changeitem" : draft.Kind == TelegramAdminRequestKind.OmitRequestIdentityOnLabel ? "labelnoid" : "labeltext")}:{request.Id:N}")
                    }).Append((IReadOnlyCollection<TelegramInlineButton>)new[]
                    {
                        new TelegramInlineButton("❌ لغو", "adminrequest:cancel")
                }).ToArray();
                await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),
                    (showingAllActiveItems
                        ? "آیدی دقیقاً تطبیق داده نشد؛ آیتم موردنظر را از تمام آیتم‌های فعال لیست انتخاب کنید:\n"
                        : $"آیتم موردنظر را برای {(draft.Kind == TelegramAdminRequestKind.RemoveSingleRequest ? "حذف" : draft.Kind == TelegramAdminRequestKind.ChangeRequestVolume ? "تغییر میل" : draft.Kind == TelegramAdminRequestKind.OmitRequestIdentityOnLabel ? "حذف آیدی از لیبل" : "ثبت نام دلخواه روی لیبل")} انتخاب کنید:\n") +
                    $"لیست {draft.PublicCode} — {draft.SalesListName}",
                    rows, ct);
                return true;
            }
            if (draft.Kind is TelegramAdminRequestKind.RemoveCustomerRequests or
                TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
            {
                var identity = input.Trim();
                var removalUsername = NormalizeAdminRequestUsername(identity);
                var removalIdentity = removalUsername is not null
                    ? $"@{removalUsername}"
                    : new string(identity.Where(char.IsDigit).ToArray());
                if (removalUsername is null && removalIdentity.Length < 5)
                {
                    await ReplyAsync(message.Chat.Id, "شناسه نامعتبر است؛ @username یا Telegram ID وارد کنید.", ct);
                    return true;
                }
                var activeRequests = await _salesListRequestRepository.GetActiveCustomerRequestsAsync(removalIdentity, ct);
                if (activeRequests.Count == 0)
                {
                    _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                    await ReplyAsync(message.Chat.Id,
                        "آیتم فعالی از این مشتری در لیست‌های باز پیدا نشد.", ct);
                    return true;
                }
                draft.Identity = removalIdentity;
                draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
                if (draft.Kind == TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
                {
                    draft.AvailableRequestIds.Clear();
                    foreach (var request in activeRequests)
                        draft.AvailableRequestIds.Add(request.Id);
                    draft.SelectedRequestIds.Clear();
                    draft.MultipleRemovalPage = 0;
                }
                _adminRequestDrafts.Set(draft);
                if (draft.Kind == TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
                {
                    await SendMultipleRemovalSelectionAsync(message.Chat.Id, draft, activeRequests, ct);
                    return true;
                }
                await SendBulkCustomerRemovalConfirmationAsync(draft, activeRequests, ct);
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
            if (draft.Kind == TelegramAdminRequestKind.ChangeRequestVolume)
            {
                var request = await _salesListRequestRepository.GetAsync(draft.SelectedRequestId, ct);
                if (request is null || request.SalesListId != draft.SalesListId ||
                    request.Status != SalesListRequestStatus.Confirmed ||
                    request.Kind != SalesListRequestKind.CurrentBottle)
                {
                    await ReplyAsync(message.Chat.Id, "آیتم فعال پیدا نشد؛ فرایند را دوباره آغاز کنید.", ct);
                    _adminRequestDrafts.Remove(message.Chat.Id, message.From.Id);
                    return true;
                }
                if (request.IsBottleOwner)
                {
                    draft.BottleType = null;
                    draft.Stage = TelegramAdminRequestStage.AwaitingConfirmation;
                    _adminRequestDrafts.Set(draft);
                    await SendChangeRequestVolumeConfirmationAsync(draft, ct);
                    return true;
                }
                draft.Stage = TelegramAdminRequestStage.AwaitingBottleType;
                _adminRequestDrafts.Set(draft);
                await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(), "نوع شیشه جدید را انتخاب کنید:",
                    new IReadOnlyCollection<TelegramInlineButton>[]
                    {
                        new[]
                        {
                            new TelegramInlineButton("نرمال", "adminrequest:changebottle:normal"),
                            new TelegramInlineButton("فانتزی", "adminrequest:changebottle:fancy")
                        },
                        new[] { new TelegramInlineButton("❌ لغو", "adminrequest:cancel") }
                    }, ct);
                return true;
            }
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
                        new[] { new TelegramInlineButton("🎁 شیشه رایگان (غیر صاحب باتل)", "adminrequest:bottle:free") },
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
        var bottle = draft.IsBottleOwner ? "\nنوع: صاحب باتل — شیشه رایگان" :
            draft.IsComplimentaryBottle ? "\nنوع شیشه: رایگان (غیر صاحب باتل)" : draft.BottleType is null ? "" :
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

    private async Task SendChangeRequestVolumeConfirmationAsync(
        TelegramAdminRequestDraft draft, CancellationToken ct)
    {
        var bottle = draft.BottleType is null
            ? "صاحب باتل — شیشه رایگان"
            : draft.BottleType == BottleType.Fancy ? "فانتزی" : "نرمال";
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"پیش‌نمایش تغییر میل آیتم:\n\nلیست: {draft.PublicCode} — {draft.SalesListName}\n" +
            $"مشتری: {draft.Identity}\nمقدار قبلی: {draft.OriginalVolumeMl} میل\n" +
            $"مقدار جدید: {draft.VolumeMl} میل\nنوع شیشه: {bottle}",
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
            [new("نت‌ها", "adminrequest:editfield:perfumenotes"), new("آکوردها", "adminrequest:editfield:accords")],
            [new("💰 قیمت، حجم کل و حداقل", "adminrequest:editfield:pricing")],
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
        if (requests.Any(value => value.Kind == SalesListRequestKind.NextBottle))
            rows.Add(new[]
            {
                new TelegramInlineButton("↕️ ویرایش یکجای ترتیب صف", "adminrequest:queue:reorder")
            });
        rows.Add(new[] { new TelegramInlineButton("❌ بستن", "adminrequest:cancel") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            $"مدیریت صاحب و صف باتل\nلیست {list.PublicCode} — {list.EnglishName}\n" +
            (rows.Count == 1 ? "صاحب یا فردی در صف ثبت نشده است." : "عملیات موردنظر را انتخاب کنید:"),
            rows, ct);
    }

    private async Task SendQueueReorderPromptAsync(
        TelegramAdminRequestDraft draft,
        IReadOnlyCollection<SalesListRequest> queue,
        CancellationToken ct)
    {
        var lines = queue.Select((request, index) => $"{index + 1}. {QueueOrderIdentity(request)}").ToArray();
        var chunk = new System.Text.StringBuilder(
            $"صف فعلی لیست {draft.PublicCode} — {draft.SalesListName}:\n\n");
        foreach (var line in lines)
        {
            if (chunk.Length + line.Length + 1 > 3500)
            {
                await ReplyAsync(draft.ChatId, chunk.ToString().TrimEnd(), ct);
                chunk.Clear();
                chunk.Append("ادامه صف:\n\n");
            }
            chunk.AppendLine(line);
        }
        if (chunk.Length > 0)
            await ReplyAsync(draft.ChatId, chunk.ToString().TrimEnd(), ct);
        await ReplyAsync(draft.ChatId,
            "صف نهایی را بدون شماره، خط‌به‌خط و به ترتیب دلخواه بفرستید.\n" +
            "• برای حذف، آن خط را نفرستید.\n" +
            "• برای افزودن، آیدی را در جای دلخواه بنویسید؛ حجم پیش‌فرض ۳۰ میل است.\n" +
            "• برای حجم دیگر بنویسید: @username 50 میل\n" +
            "• برای هدیه موجود، کل عبارت «هدیه‌دهنده for هدیه‌گیرنده» را نگه دارید.\n" +
            "• هدیه جدید باید از مسیر ثبت هدیه اضافه شود.", ct);
    }

    private static string QueueOrderIdentity(SalesListRequest request)
    {
        var giver = string.IsNullOrWhiteSpace(request.TelegramUsername)
            ? request.TelegramUserId.Trim()
            : $"@{request.TelegramUsername.Trim().TrimStart('@')}";
        if (!request.IsGift)
            return giver;
        var recipient = string.IsNullOrWhiteSpace(request.GiftRecipientTelegramUsername)
            ? request.GiftRecipientTelegramUserId?.Trim() ?? "گیرنده نامشخص"
            : $"@{request.GiftRecipientTelegramUsername.Trim().TrimStart('@')}";
        return $"{giver} for {recipient}";
    }

    private static string CanonicalQueueIdentity(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), "\\s+", " ")
            .Replace("@", string.Empty, StringComparison.Ordinal);

    private static bool TryParseQueueOrderLine(
        string line, out string identity, out int? volume, out string error)
    {
        identity = line.Trim();
        volume = null;
        error = string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(
            identity, @"^(?<identity>.+?)(?:\s+(?<volume>\d+)\s*(?:ml|میل))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["identity"].Value))
        {
            error = "فرمت خط معتبر نیست.";
            return false;
        }
        identity = match.Groups["identity"].Value.Trim();
        if (match.Groups["volume"].Success)
        {
            if (!TryParsePositiveInt(match.Groups["volume"].Value, out var parsedVolume))
            {
                error = "حجم باید عددی مثبت باشد.";
                return false;
            }
            volume = parsedVolume;
        }
        return true;
    }

    private async Task SendEditConfirmationAsync(TelegramAdminRequestDraft draft, string displayValue, CancellationToken ct) =>
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"پیش‌نمایش ویرایش لیست {draft.PublicCode} — {draft.SalesListName}\n\nمقدار جدید: {displayValue}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✅ ذخیره", "adminrequest:confirm"), new TelegramInlineButton("❌ لغو", "adminrequest:cancel") }
            }, ct);

    private async Task SendBulkCustomerRemovalConfirmationAsync(
        TelegramAdminRequestDraft draft,
        IReadOnlyCollection<SalesListRequest> requests,
        CancellationToken ct)
    {
        var detailLines = requests.Select(request =>
            $"• {request.SalesList.PublicCode} — {HtmlClipped(request.SalesList.EnglishName, 50)} — " +
            $"{DisplayUser(request)} — {request.VolumeMl} میل").ToArray();
        var chunk = new System.Text.StringBuilder("آیتم‌های فعال این مشتری:\n\n");
        foreach (var line in detailLines)
        {
            if (chunk.Length + line.Length + 1 > 3500)
            {
                await ReplyAsync(draft.ChatId, chunk.ToString().TrimEnd(), ct);
                chunk.Clear();
                chunk.Append("ادامه آیتم‌ها:\n\n");
            }
            chunk.AppendLine(line);
        }
        if (chunk.Length > 0)
            await ReplyAsync(draft.ChatId, chunk.ToString().TrimEnd(), ct);

        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            "⚠️ تأیید حذف همه آیتم‌های مشتری\n\n" +
            $"مشتری: {draft.Identity}\n" +
            $"تعداد آیتم فعال در همه لیست‌های باز یا تکمیل‌شده: {requests.Count}\n\n" +
            "با تأیید، درخواست‌ها لغو، حجم لیست‌ها اصلاح و پست‌های کانال به‌روز می‌شوند.",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("🗑 بله، همه حذف شوند", "adminrequest:confirm"),
                    new TelegramInlineButton("❌ لغو", "adminrequest:cancel")
                }
            }, ct);
    }

    private async Task SendMultipleRemovalSelectionAsync(
        long chatId,
        TelegramAdminRequestDraft draft,
        IReadOnlyCollection<SalesListRequest> requests,
        CancellationToken ct)
    {
        var message = BuildMultipleRemovalSelectionMessage(draft, requests);
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message,
            BuildMultipleRemovalSelectionButtons(draft, requests), ct);
    }

    private async Task UpdateMultipleRemovalSelectionAsync(
        TelegramMessage message,
        TelegramAdminRequestDraft draft,
        CancellationToken ct)
    {
        var requests = (await _salesListRequestRepository.GetActiveCustomerRequestsAsync(draft.Identity, ct))
            .Where(value => draft.AvailableRequestIds.Contains(value.Id))
            .ToArray();
        if (requests.Length != draft.AvailableRequestIds.Count)
        {
            _adminRequestDrafts.Remove(draft.ChatId, draft.UserId);
            await _sender.EditTextAsync(message.Chat.Id.ToString(), message.MessageId,
                "یکی از آیتم‌ها تغییر کرده است؛ فرایند را دوباره آغاز کنید.", ct);
            return;
        }
        await _sender.EditTextWithKeyboardAsync(message.Chat.Id.ToString(), message.MessageId,
            BuildMultipleRemovalSelectionMessage(draft, requests),
            BuildMultipleRemovalSelectionButtons(draft, requests), ct);
    }

    private static string BuildMultipleRemovalSelectionMessage(
        TelegramAdminRequestDraft draft,
        IReadOnlyCollection<SalesListRequest> requests) =>
        "☑️ حذف چند آیتم مشتری\n\n" +
        $"مشتری: {draft.Identity}\n" +
        $"آیتم‌های فعال: {requests.Count}\n" +
        $"انتخاب‌شده: {draft.SelectedRequestIds.Count}\n" +
        $"صفحه: {Math.Min(draft.MultipleRemovalPage + 1, Math.Max(1, (int)Math.Ceiling(requests.Count / 20d)))} از {Math.Max(1, (int)Math.Ceiling(requests.Count / 20d))}\n\n" +
        "مواردی که باید حذف شوند را تیک بزنید؛ سپس «تأیید حذف انتخاب‌شده‌ها» را بزنید.";

    private static IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>
        BuildMultipleRemovalSelectionButtons(
            TelegramAdminRequestDraft draft,
            IReadOnlyCollection<SalesListRequest> requests)
    {
        const int pageSize = 50;
        var orderedRequests = requests.OrderBy(value => value.SalesList.PublicCode).ThenBy(value => value.CreatedAt)
            .ToArray();
        var pageCount = Math.Max(1, (int)Math.Ceiling(orderedRequests.Length / (double)pageSize));
        var currentPage = Math.Clamp(draft.MultipleRemovalPage, 0, pageCount - 1);
        draft.MultipleRemovalPage = currentPage;
        var rows = orderedRequests.Skip(currentPage * pageSize).Take(pageSize)
            .Select(request => (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{(draft.SelectedRequestIds.Contains(request.Id) ? "☑️" : "⬜")} " +
                    $"{request.SalesList.PersianName} — {request.VolumeMl} میل",
                    $"adminrequest:multirem:{request.Id:N}")
            }).ToList();
        if (pageCount > 1)
        {
            var pageButtons = new List<TelegramInlineButton>();
            if (currentPage > 0)
                pageButtons.Add(new TelegramInlineButton("◀️ قبلی", $"adminrequest:multirempage:{currentPage - 1}"));
            pageButtons.Add(new TelegramInlineButton($"{currentPage + 1} / {pageCount}", "adminrequest:multirempage:" + currentPage));
            if (currentPage < pageCount - 1)
                pageButtons.Add(new TelegramInlineButton("بعدی ▶️", $"adminrequest:multirempage:{currentPage + 1}"));
            rows.Add(pageButtons);
        }
        rows.Add(new[]
        {
            new TelegramInlineButton("🗑 تأیید حذف انتخاب‌شده‌ها", "adminrequest:multiremconfirm")
        });
        rows.Add(new[]
        {
            new TelegramInlineButton("✅ پایان عملیات", "adminrequest:multiremfinish")
        });
        rows.Add(new[] { new TelegramInlineButton("❌ لغو", "adminrequest:cancel") });
        return rows;
    }

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
        if (draft.Kind == TelegramAdminRequestKind.ChangeRequestVolume)
        {
            await ApplyRequestVolumeChangeAsync(callback, draft, ct);
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
        if (draft.Kind == TelegramAdminRequestKind.RemoveMultipleCustomerRequests)
        {
            if (draft.SelectedRequestIds.Count == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "حداقل یک آیتم را انتخاب کنید.", ct, true);
                return;
            }
            try
            {
                var removedCount = draft.SelectedRequestIds.Count;
                var affectedListIds = await _salesListRequestRepository.RemoveActiveRequestsAsync(
                    draft.SelectedRequestIds, ct);
                foreach (var salesListId in affectedListIds)
                    await RefreshChannelSalesListAsync(salesListId, ct);
                var selectedRemovalAuditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
                    ? _options.AdminChatId : _options.SalesAuditChatId;
                await _sender.SendAsync(selectedRemovalAuditChatId,
                    "🗑 حذف چند آیتم مشتری\n" +
                    $"زمان: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran"):yyyy/MM/dd HH:mm:ss}\n" +
                    $"ثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
                    $"مشتری: {draft.Identity}\n" +
                    $"تعداد آیتم‌های حذف‌شده: {removedCount}\n" +
                    $"تعداد لیست‌های به‌روزشده: {affectedListIds.Count}", ct);
                draft.AvailableRequestIds.ExceptWith(draft.SelectedRequestIds);
                draft.SelectedRequestIds.Clear();
                _adminRequestDrafts.Set(draft);
                await _sender.AnswerCallbackAsync(callback.Id, "آیتم‌های انتخاب‌شده حذف شدند ✅", ct);
                if (draft.AvailableRequestIds.Count == 0)
                {
                    await _sender.EditTextWithKeyboardAsync(chatId.ToString(), callback.Message!.MessageId,
                        "همهٔ آیتم‌های فعال این مشتری حذف شده‌اند. برای بستن فرایند «پایان عملیات» را بزنید.",
                        new IReadOnlyCollection<TelegramInlineButton>[]
                        {
                            new[] { new TelegramInlineButton("✅ پایان عملیات", "adminrequest:multiremfinish") }
                        }, ct);
                    return;
                }
                await UpdateMultipleRemovalSelectionAsync(callback.Message!, draft, ct);
            }
            catch (InvalidOperationException exception)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }
        var identity = draft.Identity.Trim();
        var username = identity.StartsWith('@') ? identity.TrimStart('@') : null;
        var telegramId = username is null ? identity : $"admin-username:{username.ToLowerInvariant()}";
        var list = await _salesListRepository.GetByIdAsync(draft.SalesListId, ct);
        if (list is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "لیست پیدا نشد.", ct);
            return;
        }

        var giftRecipientUsername = draft.IsGift && draft.GiftRecipientIdentity.StartsWith('@')
            ? draft.GiftRecipientIdentity.TrimStart('@') : null;
        var giftRecipientTelegramId = draft.IsGift && !draft.GiftRecipientIdentity.StartsWith('@')
            ? new string(draft.GiftRecipientIdentity.Where(char.IsDigit).ToArray()) : null;
        var effectiveIsBottleOwner = draft.IsBottleOwner;
        if (draft.IsBottleOwner && list.HasBottleOwner)
        {
            var recipientIsCurrentOwner = draft.IsGift && await _db.SalesListRequests.AsNoTracking().AnyAsync(value =>
                value.SalesListId == list.Id && !value.IsDeleted && value.IsBottleOwner &&
                value.Status == SalesListRequestStatus.Confirmed &&
                ((!string.IsNullOrWhiteSpace(giftRecipientTelegramId) && value.TelegramUserId == giftRecipientTelegramId) ||
                 (!string.IsNullOrWhiteSpace(giftRecipientUsername) && value.TelegramUsername == giftRecipientUsername)), ct);
            if (!recipientIsCurrentOwner)
            {
                await _sender.AnswerCallbackAsync(callback.Id,
                    "این لیست قبلاً صاحب باتل دارد؛ شیشه رایگان فقط برای همان صاحب قابل ثبت است.", ct, showAlert: true);
                return;
            }

            // The gift recipient already owns the bottle. This request receives
            // the owner's free bottle without creating a second bottle owner.
            effectiveIsBottleOwner = false;
        }
        var effectiveIsComplimentaryBottle = draft.IsComplimentaryBottle ||
                                             (draft.IsBottleOwner && !effectiveIsBottleOwner);

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
        var request = new SalesListRequest
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, SalesListId = list.Id,
            TelegramUserId = telegramId, TelegramUsername = username, VolumeMl = draft.VolumeMl,
            IsGift = draft.IsGift,
            GiftRecipientTelegramUsername = giftRecipientUsername,
            GiftRecipientTelegramUserId = giftRecipientTelegramId,
            IsBottleOwner = effectiveIsBottleOwner,
            IsComplimentaryBottle = effectiveIsComplimentaryBottle,
            BottleId = bottle?.Id, PerfumePricePerMl = list.PricePerMl,
            BottlePrice = effectiveIsComplimentaryBottle ? 0 : bottle?.SalePrice ?? 0,
            Kind = draft.Kind == TelegramAdminRequestKind.NextBottle
                ? SalesListRequestKind.NextBottle : SalesListRequestKind.CurrentBottle,
            Status = draft.Kind == TelegramAdminRequestKind.NextBottle
                ? SalesListRequestStatus.Confirmed : SalesListRequestStatus.PendingConfirmation,
            CreatedByAdmin = true,
            ExpiresAt = draft.Kind == TelegramAdminRequestKind.NextBottle ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(10),
            ConfirmedAt = draft.Kind == TelegramAdminRequestKind.NextBottle ? DateTime.UtcNow : null,
            ExternalReference = $"admin-interactive:{Guid.NewGuid():N}"
        };
        try
        {
            await _salesListRequestRepository.AddAsync(request, ct);
            await _salesListRequestRepository.SaveChangesAsync(ct);
            if (draft.Kind is TelegramAdminRequestKind.CustomRequest or TelegramAdminRequestKind.GiftRequest)
                await _salesListRequestRepository.ConfirmCurrentBottleAsync(request.Id, telegramId, ct);
        }
        catch (InvalidOperationException exception)
        {
            request.Status = SalesListRequestStatus.Cancelled;
            request.IsDeleted = true;
            request.UpdatedAt = DateTime.UtcNow;
            await _salesListRequestRepository.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, showAlert: true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست ثبت شد ✅", ct);
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
                : draft.IsComplimentaryBottle
                ? $"{BottleLabel(bottle!.Type.ToString())} — رایگان"
                : bottle is null
                ? "نامشخص"
                : $"{BottleLabel(bottle.Type.ToString())} — {bottle.SalePrice:N0} تومان";
            var total = draft.VolumeMl * list.PricePerMl +
                        (draft.IsComplimentaryBottle ? 0 : bottle?.SalePrice ?? 0);
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
        await ReplyAsync(chatId, "درخواست با موفقیت ثبت و لیست فروش به‌روزرسانی شد ✅", ct);
    }

    private async Task ApplyRequestVolumeChangeAsync(
        TelegramCallbackQuery callback, TelegramAdminRequestDraft draft, CancellationToken ct)
    {
        var request = await _salesListRequestRepository.GetAsync(draft.SelectedRequestId, ct);
        if (request is null || request.SalesListId != draft.SalesListId ||
            request.Status != SalesListRequestStatus.Confirmed ||
            request.Kind != SalesListRequestKind.CurrentBottle)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "آیتم فعال پیدا نشد.", ct, true);
            _adminRequestDrafts.Remove(draft.ChatId, draft.UserId);
            return;
        }

        Bottle? bottle = null;
        if (!request.IsBottleOwner)
        {
            if (draft.BottleType is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "نوع شیشه را انتخاب کنید.", ct, true);
                return;
            }
            var bottles = await _mediator.Send(
                new ZibasheERP.Application.Features.Bottles.GetAvailableBottles.GetAvailableBottlesQuery(draft.VolumeMl), ct);
            var summary = bottles.FirstOrDefault(value =>
                string.Equals(value.Type, draft.BottleType.ToString(), StringComparison.OrdinalIgnoreCase));
            if (summary is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id,
                    "برای مقدار و نوع شیشه انتخاب‌شده، شیشه فعال پیدا نشد.", ct, true);
                return;
            }
            bottle = await _bottleRepository.GetByIdAsync(summary.Id, ct);
            if (bottle is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "شیشه انتخاب‌شده دیگر فعال نیست.", ct, true);
                return;
            }
        }

        try
        {
            await _salesListRequestRepository.UpdateConfirmedCurrentBottleRequestAsync(
                request.Id, draft.VolumeMl, bottle?.Id,
                request.IsComplimentaryBottle ? 0 : bottle?.SalePrice ?? 0, ct);
            await RefreshChannelSalesListAsync(request.SalesListId, ct);
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            return;
        }

        var auditChatId = string.IsNullOrWhiteSpace(_options.SalesAuditChatId)
            ? _options.AdminChatId : _options.SalesAuditChatId;
        var bottleLabel = request.IsBottleOwner
            ? "صاحب باتل — شیشه رایگان"
            : request.IsComplimentaryBottle
            ? $"{BottleLabel(bottle!.Type.ToString())} — رایگان"
            : $"{BottleLabel(bottle!.Type.ToString())} — {bottle.SalePrice:N0} تومان";
        await _sender.SendAsync(auditChatId,
            "↕️ تغییر میل آیتم لیست فروش\n" +
            $"زمان: {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Asia/Tehran"):yyyy/MM/dd HH:mm:ss}\n" +
            $"ثبت‌کننده: {DisplayTelegramUser(callback.From)}\n" +
            $"لیست: {draft.PublicCode} — {draft.SalesListName}\n" +
            $"مشتری: {DisplayUser(request)}\n" +
            $"مقدار: {draft.OriginalVolumeMl} ← {draft.VolumeMl} میل\n" +
            $"شیشه: {bottleLabel}\n" +
            $"درخواست: {request.Id:N}", ct);
        _adminRequestDrafts.Remove(draft.ChatId, draft.UserId);
        await _sender.AnswerCallbackAsync(callback.Id, "آیتم به‌روزرسانی شد ✅", ct);
        await ReplyAsync(draft.ChatId, "مقدار و نوع شیشه آیتم به‌روزرسانی و پست کانال اصلاح شد ✅", ct);
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
                case "perfumenotes" when TryParseCombinedNotes(draft.EditValue, out var top, out var middle, out var bottom):
                    list.TopNotes = top;
                    list.MiddleNotes = middle;
                    list.BaseNotes = bottom;
                    break;
                case "perfumenotes": throw new InvalidOperationException("یک خط برای تک‌نت یا دقیقاً سه خط برای نت ابتدایی، میانی و پایانی بفرستید.");
                case "accords": list.Accords = draft.EditValue.Trim(); break;
                case "pricing" when TryParseSalesListPricing(draft.EditValue, out var price, out var total,
                    out var minimum, out _):
                    if (total < list.ReservedVolume)
                        throw new InvalidOperationException($"حجم کل نمی‌تواند از حجم ثبت‌شده فعلی ({list.ReservedVolume:N0} میل) کمتر باشد.");
                    list.PricePerMl = price;
                    list.TotalVolume = total;
                    list.MinimumRequestVolumeMl = minimum;
                    break;
                case "pricing": throw new InvalidOperationException("قیمت، حجم کل یا حداقل حجم معتبر نیست.");
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
            var perfumes = await _perfumeRepository.GetAllActiveForPriceUpdateAsync(ct);
            foreach (var perfume in perfumes)
            {
                perfume.PricePerMl = AdjustedPrice(perfume.PricePerMl, draft.Value);
                perfume.UpdatedAt = DateTime.UtcNow;
            }
            await _perfumeRepository.SaveChangesAsync(ct);

            var openLists = await _salesListRepository.GetAllOpenForPriceUpdateAsync(ct);
            foreach (var list in openLists)
            {
                list.PricePerMl = AdjustedPrice(list.PricePerMl, draft.Value);
                list.UpdatedAt = DateTime.UtcNow;
            }
            await _salesListRepository.SaveChangesAsync(ct);
            _salesListRebuildWorker.TryQueue(callback.Message.Chat.Id);
        }
        _ownerPricingDrafts.Remove(callback.Message.Chat.Id, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, "تغییر قیمت اعمال شد ✅", ct);
        await ReplyAsync(callback.Message.Chat.Id, "تغییر قیمت با موفقیت اعمال شد ✅", ct);
    }

    private static string? NormalizeAdminRequestUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        var telegramLinkIndex = candidate.LastIndexOf("t.me/", StringComparison.OrdinalIgnoreCase);
        if (telegramLinkIndex >= 0)
            candidate = candidate[(telegramLinkIndex + 5)..];
        candidate = candidate.TrimStart('@');
        var normalized = new string(candidate
            .Where(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            .ToArray());
        return normalized.Length > 0 && normalized.Any(char.IsLetter)
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static decimal AdjustedPrice(decimal price, decimal percent) =>
        Math.Round(price * (1 + percent / 100m), 0, MidpointRounding.AwayFromZero);
}
