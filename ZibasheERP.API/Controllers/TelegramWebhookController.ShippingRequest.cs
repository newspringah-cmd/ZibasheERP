using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private async Task<bool> TryHandleShippingRequestCallbackAsync(
        TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.Data is null ||
            !callback.Data.StartsWith("shipping:", StringComparison.Ordinal))
            return false;

        if (callback.Data == "shipping:request")
        {
            if (!string.Equals(callback.Message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "درخواست پست فقط در گفت‌وگوی خصوصی ربات ثبت می‌شود.", ct, true);
                return true;
            }
            await CreateCustomerShippingRequestAsync(callback, ct);
            return true;
        }

        if (callback.Data == "shipping:prepare")
        {
            if (!IsAuthorizedShippingOperator(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای مدیر و حسابدار فعال است.", ct, true);
                return true;
            }
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            var isLinkedCustomerGroup = await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(value =>
                !value.IsDeleted && value.IsActive && value.ChatId == callback.Message.Chat.Id.ToString(), ct);
            if (isLinkedCustomerGroup)
            {
                await StartShippingPreparationAsync(callback.Message.Chat.Id, callback.From.Id, ct);
                return true;
            }
            _orderFlowDrafts.SetShippingPreparation(new TelegramShippingPreparationDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id,
                Stage = TelegramShippingPreparationStage.AwaitingIdentity,
                AllowUnlinkedChat = true
            });
            await ReplyAsync(callback.Message.Chat.Id,
                "آیدی مشتری را به‌صورت @username یا Telegram ID ارسال کنید.", ct);
            return true;
        }

        if (callback.Data == "shipping:manualaddress")
        {
            if (!IsAuthorizedShippingOperator(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای مدیر و حسابدار فعال است.", ct, true);
                return true;
            }
            _orderFlowDrafts.SetShippingPreparation(new TelegramShippingPreparationDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id,
                Stage = TelegramShippingPreparationStage.AwaitingIdentity,
                RegistrationOnly = true,
                AllowUnlinkedChat = true
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id,
                "آیدی مشتری را به‌صورت @username یا Telegram ID ارسال کنید.", ct);
            return true;
        }

        if (callback.Data.StartsWith("shipping:preparecustomer:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:preparecustomer:".Length..], "N", out var preparedCustomerId))
        {
            if (!_orderFlowDrafts.TryGetShippingPreparation(callback.Message.Chat.Id, callback.From.Id, out var draft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
                return true;
            }
            var linked = await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(value =>
                !value.IsDeleted && value.IsActive && value.ChatId == callback.Message.Chat.Id.ToString() &&
                value.CustomerId == preparedCustomerId, ct);
            if (!linked)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "اتصال این مشتری دیگر معتبر نیست.", ct, true);
                return true;
            }
            draft.CustomerId = preparedCustomerId;
            draft.Stage = TelegramShippingPreparationStage.AwaitingAddressChoice;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendShippingAddressChoicesAsync(draft, ct);
            return true;
        }

        if (callback.Data == "shipping:preparenew")
        {
            if (!_orderFlowDrafts.TryGetShippingPreparation(callback.Message.Chat.Id, callback.From.Id, out var draft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
                return true;
            }
            draft.Stage = TelegramShippingPreparationStage.AwaitingNewAddress;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(callback.Message.Chat.Id, "آدرس کامل را وارد کنید", ct);
            return true;
        }

        if (callback.Data.StartsWith("shipping:prepareaddress:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:prepareaddress:".Length..], "N", out var preparedAddressId))
        {
            if (!_orderFlowDrafts.TryGetShippingPreparation(callback.Message.Chat.Id, callback.From.Id, out var draft))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
                return true;
            }
            draft.AddressId = preparedAddressId;
            draft.Stage = TelegramShippingPreparationStage.Ready;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendShippingPreparationPreviewAsync(draft, ct);
            return true;
        }

        if (callback.Data == "shipping:preparedispatch")
        {
            await DispatchPreparedShippingRequestAsync(callback, ct);
            return true;
        }

        if (callback.Data.StartsWith("shipping:select:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:select:".Length..], "N", out var selectedRequestId))
        {
            if (!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
                return true;
            }
            var selected = _orderFlowDrafts.GetShippingSelection(callback.Message.Chat.Id, callback.From.Id);
            var added = selected.Add(selectedRequestId);
            if (!added) selected.Remove(selectedRequestId);
            await _sender.AnswerCallbackAsync(callback.Id,
                added ? $"انتخاب شد؛ مجموع انتخاب‌ها: {selected.Count}" : $"از انتخاب خارج شد؛ مجموع: {selected.Count}", ct, true);
            return true;
        }

        if (callback.Data == "shipping:batchreport")
        {
            await SendSelectedShippingReportAsync(callback, ct);
            return true;
        }

        if (callback.Data == "shipping:trackingdone")
        {
            await _sender.AnswerCallbackAsync(callback.Id, "کد رهگیری قبلاً ارسال شده است ✅", ct, true);
            return true;
        }

        var isBatchTracking = callback.Data.StartsWith("shipping:trackingbatch:", StringComparison.Ordinal);
        var trackingPrefix = isBatchTracking ? "shipping:trackingbatch:" : "shipping:tracking:";
        if (callback.Data.StartsWith(trackingPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[trackingPrefix.Length..], "N", out var trackingRequestId))
        {
            if (!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
                return true;
            }
            var trackingItem = await _db.OrderItems.AsNoTracking()
                .Include(value => value.Order)
                .FirstOrDefaultAsync(value => !value.IsDeleted &&
                    value.ShippingRequestId == trackingRequestId && value.Order != null, ct);
            if (trackingItem?.Order is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "اطلاعات مشتری این درخواست پیدا نشد.", ct, true);
                return true;
            }
            _orderFlowDrafts.SetShippingTrackingPhoto(new TelegramShippingTrackingPhotoDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id,
                CustomerId = trackingItem.Order.CustomerId,
                ShippingRequestId = trackingRequestId,
                SourceMessageId = callback.Message.MessageId,
                HasBatchControls = isBatchTracking
            });
            await _sender.AnswerCallbackAsync(callback.Id, "عکس کد رهگیری را ارسال کنید.", ct, true);
            await ReplyAsync(callback.Message.Chat.Id,
                "📸 عکس کد رهگیری همین مشتری را ارسال کنید. برای لغو، پیام /cancel را بفرستید.", ct);
            return true;
        }

        if (!callback.Data.StartsWith("shipping:sent:", StringComparison.Ordinal) ||
            !Guid.TryParseExact(callback.Data["shipping:sent:".Length..], "N", out var requestId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال نامعتبر است.", ct);
            return true;
        }
        if (!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
            return true;
        }

        var items = await _db.OrderItems.Include(value => value.Order)
            .Where(value => !value.IsDeleted && value.ShippingRequestId == requestId).ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "اقلام این درخواست پیدا نشد.", ct, true);
            return true;
        }
        if (items.All(value => value.FulfillmentStatus == OrderItemFulfillmentStatus.Shipped))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این درخواست قبلاً ارسال‌شده ثبت شده است ✅", ct, true);
            return true;
        }
        if (items.Any(value => value.FulfillmentStatus != OrderItemFulfillmentStatus.DecantedReadyToShip))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "وضعیت بعضی اقلام تغییر کرده؛ عملیات متوقف شد.", ct, true);
            return true;
        }
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.FulfillmentStatus = OrderItemFulfillmentStatus.Shipped;
            item.ShippedAt = now;
            item.UpdatedAt = now;
        }
        foreach (var order in items.Select(value => value.Order).Where(value => value is not null).Distinct()!)
        {
            var hasUnshipped = await _db.OrderItems.AnyAsync(value => !value.IsDeleted && value.OrderId == order!.Id &&
                value.ShippingRequestId != requestId && value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped, ct);
            if (!hasUnshipped)
            {
                order!.Status = OrderStatus.Shipped;
                order.ShippedAt = now;
                order.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        await _sender.AnswerCallbackAsync(callback.Id, $"{items.Length} آیتم ارسال‌شده ثبت شد ✅", ct, true);
        await ReplyAsync(callback.Message.Chat.Id, $"✅ درخواست ارسال با {items.Length} آیتم تکمیل شد.", ct);
        return true;
    }

    private async Task CreateCustomerShippingRequestAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var telegramId = callback.From.Id.ToString();
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.TelegramId == telegramId, ct);
        if (customer is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "حساب مشتری متصل نیست.", ct, true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "گروه مسئول ارسال هنوز تنظیم نشده است.", ct, true);
            return;
        }
        var existing = await _db.OrderItems.AsNoTracking().FirstOrDefaultAsync(value => !value.IsDeleted &&
            value.Order != null && value.Order.CustomerId == customer.Id &&
            value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip &&
            value.ShippingRequestId != null, ct);
        if (existing is not null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال این اقلام قبلاً ثبت شده و در انتظار مسئول ارسال است.", ct, true);
            return;
        }
        var items = await _db.OrderItems
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList)
            .Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.CustomerId == customer.Id &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip &&
                value.ShippingRequestId == null).OrderBy(value => value.CreatedAt).ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "آیتم آماده ارسالی پیدا نشد.", ct, true);
            return;
        }
        var address = await _db.Addresses.AsNoTracking().Where(value => !value.IsDeleted && value.CustomerId == customer.Id)
            .OrderByDescending(value => value.IsDefault).ThenByDescending(value => value.UpdatedAt ?? value.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (address is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ابتدا آدرس ارسال را در بخش آدرس‌های من ثبت کنید.", ct, true);
            return;
        }
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.ShippingRequestId = requestId;
            item.ShippingRequestedAt = now;
            item.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        var lines = items.Select((item, index) =>
            $"{index + 1}. {item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر"} — {item.RequestedVolumeMl} میل");
        var message = $"📦 درخواست ارسال جدید\n\nمشتری: {OrderCustomerLabel(customer)}\n" +
            $"گیرنده: {address.ReceiverName}\nموبایل: {address.Mobile}\nکدپستی: {address.PostalCode}\n" +
            $"آدرس: {address.Province}، {address.City}، {address.FullAddress}\n\nاقلام آماده ارسال:\n{string.Join("\n", lines)}";
        var sent = await _sender.SendInlineKeyboardAsync(_options.ShippingChatId.Trim(), message,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton("✅ ارسال شد", $"shipping:sent:{requestId:N}"),
                    new TelegramInlineButton("📸 ارسال کد رهگیری", $"shipping:tracking:{requestId:N}")
                }
            }, ct);
        if (!sent.IsSuccessful)
        {
            foreach (var item in items)
            {
                item.ShippingRequestId = null;
                item.ShippingRequestedAt = null;
                item.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, $"ارسال درخواست به مسئول ارسال ناموفق بود: {sent.Error}", ct, true);
            return;
        }
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال ثبت شد ✅", ct, true);
        await SendAddressLabelCopyAsync(requestId, customer, address, callback.Message!.Chat.Id, ct);
        await ReplyAsync(callback.Message!.Chat.Id,
            $"درخواست ارسال {items.Length} آیتم آماده برای مسئول ارسال ثبت شد ✅", ct);
    }

    private async Task<bool> TryHandleShippingPreparationMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_orderFlowDrafts.TryGetShippingPreparation(message.Chat.Id, message.From.Id, out var draft))
            return false;
        var authorized = draft.AllowUnlinkedChat
            ? IsAuthorizedShippingOperator(message.From.Id)
            : await IsAuthorizedAccountingShippingAdminAsync(message.Chat.Id, message.From.Id, ct);
        if (!authorized)
        {
            _orderFlowDrafts.ClearShippingPreparation(message.Chat.Id, message.From.Id);
            return true;
        }
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (draft.Stage == TelegramShippingPreparationStage.AwaitingIdentity)
        {
            var username = input.TrimStart('@');
            var customer = input.StartsWith('@')
                ? await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
                    (value.Username == username || value.Username == "@" + username), ct)
                : await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted && value.TelegramId == input, ct);
            if (customer is null)
            {
                await ReplyAsync(message.Chat.Id, "مشتری با این آیدی پیدا نشد.", ct);
                return true;
            }
            draft.CustomerId = customer.Id;
            draft.Stage = draft.RegistrationOnly
                ? TelegramShippingPreparationStage.AwaitingNewAddress
                : TelegramShippingPreparationStage.AwaitingAddressChoice;
            _orderFlowDrafts.SetShippingPreparation(draft);
            if (draft.RegistrationOnly)
                await ReplyAsync(message.Chat.Id,
                    $"مشتری: {OrderCustomerLabel(customer)}\n\nکل متن آدرس را در یک پیام ارسال کنید. نام گیرنده، موبایل و شهر مقصد الزامی است؛ کدپستی اختیاری است.", ct);
            else
                await SendShippingAddressChoicesAsync(draft, ct);
            return true;
        }
        if (draft.Stage == TelegramShippingPreparationStage.AwaitingNewAddress)
        {
            if (input.Length > 1000)
            {
                await ReplyAsync(message.Chat.Id, "متن آدرس حداکثر می‌تواند ۱۰۰۰ کاراکتر باشد.", ct);
                return true;
            }
            var customer = await _db.Customers.AsNoTracking()
                .FirstAsync(value => value.Id == draft.CustomerId && !value.IsDeleted, ct);
            var hasAddress = await _db.Addresses.AnyAsync(value => !value.IsDeleted && value.CustomerId == draft.CustomerId, ct);
            var address = new Address
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CustomerId = draft.CustomerId,
                ReceiverName = customer.FullName, Mobile = customer.Mobile,
                Province = string.Empty, City = string.Empty, PostalCode = string.Empty,
                FullAddress = input, Description = "آدرس خام ثبت‌شده توسط حسابدار",
                IsDefault = !hasAddress
            };
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync(ct);
            if (draft.RegistrationOnly)
            {
                _orderFlowDrafts.ClearShippingPreparation(message.Chat.Id, message.From.Id);
                await ReplyAsync(message.Chat.Id,
                    $"✅ آدرس برای {OrderCustomerLabel(customer)} ثبت شد.", ct);
                return true;
            }
            draft.AddressId = address.Id;
            draft.Stage = TelegramShippingPreparationStage.Ready;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingPreparationPreviewAsync(draft, ct);
            return true;
        }
        await ReplyAsync(message.Chat.Id, "از دکمه‌های فرایند ارسال استفاده کنید.", ct);
        return true;
    }

    private async Task<bool> TryHandleShippingPreparationCommandAsync(TelegramMessage message, CancellationToken ct)
    {
        var command = message.Text?.Trim().Split((char[]?)null, 2,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('@', 2)[0];
        if (!string.Equals(command, "/ad", StringComparison.OrdinalIgnoreCase)) return false;
        if (message.From is null || !IsAuthorizedShippingOperator(message.From.Id))
        {
            await ReplyAsync(message.Chat.Id,
                "دستور /ad فقط برای مدیر و حسابدار مجاز فعال است.", ct);
            return true;
        }
        if (!await EnsureActiveCustomerGroupLinkAsync(message.Chat, ct))
        {
            await ReplyAsync(message.Chat.Id,
                "این گروه به مشتری متصل نیست. ابتدا دستور اتصال گروه را دوباره داخل همین گروه ارسال کنید.", ct);
            return true;
        }
        await StartShippingPreparationAsync(message.Chat.Id, message.From.Id, ct);
        return true;
    }

    private async Task<bool> EnsureActiveCustomerGroupLinkAsync(TelegramChat chat, CancellationToken ct)
    {
        var chatId = chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var exactLinks = await _db.CustomerTelegramGroups
            .Where(value => !value.IsDeleted && value.ChatId == chatId)
            .ToArrayAsync(ct);
        if (exactLinks.Length > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var link in exactLinks)
            {
                link.IsActive = true;
                link.LastSeenAt = now;
                link.UpdatedAt = now;
                if (!string.IsNullOrWhiteSpace(chat.Title)) link.Title = chat.Title.Trim();
                if (!string.IsNullOrWhiteSpace(chat.Username)) link.Username = chat.Username.Trim().TrimStart('@');
            }
            await _db.SaveChangesAsync(ct);
            return true;
        }

        // Telegram may omit the migration service message from the webhook history.
        // Recover only an unambiguous basic-group -> supergroup mapping with the same title.
        if (string.Equals(chat.Type, "supergroup", StringComparison.OrdinalIgnoreCase) &&
            chatId.StartsWith("-100", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(chat.Title))
        {
            var title = chat.Title.Trim();
            var candidates = await _db.CustomerTelegramGroups.AsNoTracking()
                .Where(value => !value.IsDeleted && value.Title == title &&
                    !value.ChatId.StartsWith("-100"))
                .Select(value => value.ChatId)
                .Distinct()
                .Take(2)
                .ToArrayAsync(ct);
            if (candidates.Length == 1 && long.TryParse(candidates[0], out var oldChatId))
            {
                await _groupMembershipTracker.TrackMigrationAsync(oldChatId, chat, ct);
                return await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(value =>
                    !value.IsDeleted && value.IsActive && value.ChatId == chatId, ct);
            }
        }
        return false;
    }

    private async Task StartShippingPreparationAsync(long chatId, long userId, CancellationToken ct)
    {
        var linkedCustomers = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive && value.ChatId == chatId.ToString())
            .Include(value => value.Customer)
            .OrderBy(value => value.Customer.FullName).ToArrayAsync(ct);
        if (linkedCustomers.Length == 0)
        {
            await ReplyAsync(chatId, "این گروه هنوز به مشتری متصل نشده است.", ct);
            return;
        }
        var draft = new TelegramShippingPreparationDraft
        {
            ChatId = chatId, UserId = userId,
            Stage = TelegramShippingPreparationStage.AwaitingAddressChoice
        };
        _orderFlowDrafts.SetShippingPreparation(draft);
        if (linkedCustomers.Length == 1)
        {
            draft.CustomerId = linkedCustomers[0].CustomerId;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingAddressChoicesAsync(draft, ct);
            return;
        }
        var buttons = linkedCustomers.Select(value =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(OrderCustomerLabel(value.Customer),
                    $"shipping:preparecustomer:{value.CustomerId:N}")
            }).ToArray();
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "این گروه به چند مشتری متصل است؛ مشتری را انتخاب کنید:", buttons, ct);
    }

    private async Task SendShippingAddressChoicesAsync(TelegramShippingPreparationDraft draft, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var addresses = await _db.Addresses.AsNoTracking().Where(value => !value.IsDeleted && value.CustomerId == draft.CustomerId)
            .OrderByDescending(value => value.IsDefault).ThenByDescending(value => value.CreatedAt).Take(10).ToArrayAsync(ct);
        var buttons = addresses.Select(address => (IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton($"{address.ReceiverName} — {address.City}{(address.IsDefault ? " ✅" : "")}",
                $"shipping:prepareaddress:{address.Id:N}")
        }).ToList();
        buttons.Add(new[] { new TelegramInlineButton("➕ ثبت آدرس جدید", "shipping:preparenew") });
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"📮 آماده‌سازی پست\nمشتری: {OrderCustomerLabel(customer)}\n\nآدرس را بررسی و انتخاب کنید:", buttons, ct);
    }

    private async Task SendShippingPreparationPreviewAsync(TelegramShippingPreparationDraft draft, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstAsync(value => value.Id == draft.AddressId, ct);
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"📍 آدرس\n\nمشتری: {OrderCustomerLabel(customer)}\n\n{FormatAddressForDisplay(address)}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("📮 ارسال آدرس به گروه پست", "shipping:preparedispatch") }
            }, ct);
    }

    private async Task DispatchPreparedShippingRequestAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (!_orderFlowDrafts.TryGetShippingPreparation(callback.Message!.Chat.Id, callback.From.Id, out var draft) ||
            draft.Stage != TelegramShippingPreparationStage.Ready || !draft.AddressId.HasValue)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            await _sender.AnswerCallbackAsync(callback.Id,
                "گروه مسئول پست تنظیم نشده است؛ ابتدا Telegram__ShippingChatId را تنظیم کنید.", ct, true);
            return;
        }
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstAsync(value => value.Id == draft.AddressId, ct);
        var items = await ReadyShippingItems(draft.CustomerId).ToArrayAsync(ct);
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var item in items) { item.ShippingRequestId = requestId; item.ShippingRequestedAt = now; item.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        var identity = OrderCustomerLabel(customer);
        var shippingMessage = $"📦 درخواست ارسال جدید\n\nآیدی: {identity}\n\n{FormatAddressForDisplay(address)}";
        var shippingButtons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        if (items.Length > 0)
        {
            shippingButtons.Add(new[] { new TelegramInlineButton("☑️ انتخاب برای گزارش کلی", $"shipping:select:{requestId:N}") });
            shippingButtons.Add(new[] { new TelegramInlineButton("📊 گزارش کلی انتخاب‌ها", "shipping:batchreport") });
            shippingButtons.Add(new[]
            {
                new TelegramInlineButton("✅ ارسال شد", $"shipping:sent:{requestId:N}"),
                new TelegramInlineButton("📸 ارسال کد رهگیری", $"shipping:trackingbatch:{requestId:N}")
            });
        }
        else
        {
            // Legacy address-only requests have no persisted shipment items to attach tracking to.
        }
        var sent = await _sender.SendInlineKeyboardAsync(
            _options.ShippingChatId.Trim(), shippingMessage, shippingButtons, ct);
        if (!sent.IsSuccessful)
        {
            foreach (var item in items) { item.ShippingRequestId = null; item.ShippingRequestedAt = null; }
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, $"ارسال ناموفق بود: {sent.Error}", ct, true);
            return;
        }
        _orderFlowDrafts.ClearShippingPreparation(callback.Message.Chat.Id, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id,
            "برای گروه پست ارسال شد ✅ لیبل در حال آماده‌سازی است.", ct, true);
        await SendAddressLabelCopyAsync(requestId, customer, address, callback.Message.Chat.Id, ct);
    }

    private async Task SendSelectedShippingReportAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        if (!await IsAuthorizedShippingAdminAsync(callback.Message!.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
            return;
        }
        var selected = _orderFlowDrafts.GetShippingSelection(callback.Message.Chat.Id, callback.From.Id);
        if (selected.Count == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ابتدا چند درخواست را انتخاب کنید.", ct, true);
            return;
        }
        var items = await _db.OrderItems.AsNoTracking().Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList).Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.ShippingRequestId.HasValue &&
                selected.Contains(value.ShippingRequestId.Value) &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip).ToArrayAsync(ct);
        var groups = items.GroupBy(ShippingItemName).OrderBy(value => value.Key).Select(group =>
        {
            var customers = group.GroupBy(value => value.Order!.CustomerId).Select(customerGroup =>
                $"  • {OrderCustomerLabel(customerGroup.First().Order!.Customer)} — {customerGroup.Sum(value => value.RequestedVolumeMl)} میل");
            return $"🧴 {group.Key}\nتعداد نفر: {group.Select(value => value.Order!.CustomerId).Distinct().Count()} | مجموع: {group.Sum(value => value.RequestedVolumeMl)} میل\n" +
                   string.Join("\n", customers);
        });
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        await ReplyAsync(callback.Message.Chat.Id,
            $"📊 گزارش کلی ارسال\nدرخواست انتخاب‌شده: {selected.Count}\nتعداد کل دکانت: {items.Length}\nمجموع میل: {items.Sum(value => value.RequestedVolumeMl)}", ct);
        await SendShippingTextChunksAsync(callback.Message.Chat.Id, "تفکیک عطرها:", groups, ct);
    }

    private async Task<bool> TryHandleShippingTrackingPhotoMessageAsync(
        TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_orderFlowDrafts.TryGetShippingTrackingPhoto(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedShippingAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id, message.From.Id);
            return true;
        }
        if (string.Equals(message.Text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ارسال کد رهگیری لغو شد.", ct);
            return true;
        }
        var photo = message.Photo?.OrderByDescending(value => value.FileSize ?? 0).FirstOrDefault();
        if (photo is null)
        {
            await ReplyAsync(message.Chat.Id, "لطفاً کد رهگیری را به‌صورت عکس ارسال کنید یا /cancel را بفرستید.", ct);
            return true;
        }
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(value =>
            value.Id == draft.CustomerId && !value.IsDeleted, ct);
        var recipients = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive && value.CustomerId == draft.CustomerId)
            .Select(value => value.ChatId).Distinct().ToArrayAsync(ct);
        if (customer is null || recipients.Length == 0)
        {
            _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id,
                $"⚠️ گروه فعالی برای مشتری {(customer is null ? draft.CustomerId.ToString("N") : OrderCustomerLabel(customer))} پیدا نشد.", ct);
            return true;
        }
        var failures = new List<string>();
        foreach (var recipient in recipients)
        {
            var result = await _sender.SendPhotoAsync(recipient, photo.FileId,
                "📦 تصویر کد رهگیری مرسوله شما", ct);
            if (!result.IsSuccessful) failures.Add($"{recipient}: {result.Error}");
        }
        _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id, message.From.Id);
        if (failures.Count > 0)
        {
            await ReplyAsync(message.Chat.Id,
                $"⚠️ ارسال عکس کد رهگیری برای {OrderCustomerLabel(customer)} کامل نشد:\n{string.Join("\n", failures)}", ct);
            return true;
        }
        var updatedButtons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        if (draft.HasBatchControls)
        {
            updatedButtons.Add(new[]
            {
                new TelegramInlineButton("☑️ انتخاب برای گزارش کلی", $"shipping:select:{draft.ShippingRequestId:N}")
            });
            updatedButtons.Add(new[]
            {
                new TelegramInlineButton("📊 گزارش کلی انتخاب‌ها", "shipping:batchreport")
            });
        }
        updatedButtons.Add(new[]
        {
            new TelegramInlineButton("✅ ارسال شد", $"shipping:sent:{draft.ShippingRequestId:N}"),
            new TelegramInlineButton("✅ کد ارسال شد", "shipping:trackingdone")
        });
        var markupResult = await _sender.EditReplyMarkupAsync(
            message.Chat.Id.ToString(), draft.SourceMessageId, updatedButtons, ct);
        if (!markupResult.IsSuccessful)
            await ReplyAsync(message.Chat.Id,
                $"⚠️ عکس کد رهگیری ارسال شد اما وضعیت دکمه بروزرسانی نشد: {markupResult.Error}", ct);
        await ReplyAsync(message.Chat.Id,
            $"✅ عکس کد رهگیری برای {OrderCustomerLabel(customer)} ارسال شد.", ct);
        return true;
    }

    private IQueryable<OrderItem> ReadyShippingItems(Guid customerId) =>
        _db.OrderItems.Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList).Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.CustomerId == customerId &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && value.ShippingRequestId == null)
            .OrderBy(value => value.CreatedAt);

    private static string ShippingItemName(OrderItem item) =>
        item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر";

    private static string FormatAddressForDisplay(Address address) =>
        string.Equals(address.Description, "آدرس خام ثبت‌شده توسط حسابدار", StringComparison.Ordinal)
            ? address.FullAddress
            : $"گیرنده: {address.ReceiverName}\nموبایل: {address.Mobile}\nکدپستی: {address.PostalCode}\n" +
              $"آدرس: {address.Province}، {address.City}، {address.FullAddress}";

    private async Task SendAddressLabelCopyAsync(
        Guid requestId, Customer customer, Address address, long warningChatId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AddressLabelPrintChatId)) return;
        if (_addressLabelService.IsEnabled)
        {
            var label = await _addressLabelService.CreateAsync(address, ct);
            if (!label.IsSuccessful || label.Pdf is null || label.Preview is null)
            {
                await ReportAddressLabelFailureAsync(requestId, customer, warningChatId,
                    $"لیبل ساخته نشد: {label.Error}", ct);
                return;
            }

            var registeredAddress = string.Equals(
                    address.Description, "آدرس خام ثبت‌شده توسط حسابدار", StringComparison.Ordinal)
                ? address.FullAddress
                : string.Join("\n", new[]
                {
                    $"نام گیرنده: {address.ReceiverName}",
                    $"تلفن: {address.Mobile}",
                    string.IsNullOrWhiteSpace(address.PostalCode) ? null : $"کدپستی: {address.PostalCode}",
                    string.IsNullOrWhiteSpace(address.Province) ? null : $"استان: {address.Province}",
                    string.IsNullOrWhiteSpace(address.City) ? null : $"شهر: {address.City}",
                    $"نشانی: {address.FullAddress}"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var reviewResult = await _sender.SendAsync(
                _options.AddressLabelPrintChatId.Trim(),
                $"📝 آدرس اصلی ثبت‌شده برای مقایسه\n" +
                $"آیدی مشتری: {OrderCustomerLabel(customer)}\n\n{registeredAddress}", ct);
            if (!reviewResult.IsSuccessful)
                await ReportAddressLabelFailureAsync(requestId, customer, warningChatId,
                    $"متن آدرس اصلی برای مقایسه ارسال نشد: {reviewResult.Error}", ct);

            var previewResult = await _sender.SendPhotoBytesWithKeyboardAsync(
                _options.AddressLabelPrintChatId.Trim(), label.Preview,
                $"address-label-{requestId:N}.jpg", $"👁 پیش‌نمایش لیبل {OrderCustomerLabel(customer)}",
                Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
            if (!previewResult.IsSuccessful)
                await ReportAddressLabelFailureAsync(requestId, customer, warningChatId,
                    $"PDF لیبل ساخته شد اما پیش‌نمایش آن ارسال نشد: {previewResult.Error}", ct);

            var pdfResult = await _sender.SendDocumentWithKeyboardAsync(
                _options.AddressLabelPrintChatId.Trim(), label.Pdf,
                $"address-label-{requestId:N}.pdf", $"🏷 لیبل آدرس {OrderCustomerLabel(customer)}",
                Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), ct);
            if (!pdfResult.IsSuccessful)
                await ReportAddressLabelFailureAsync(requestId, customer, warningChatId,
                    $"PDF لیبل ارسال نشد: {pdfResult.Error}", ct);
            return;
        }
        var identity = OrderCustomerLabel(customer);
        var rawAddress = string.Equals(address.Description, "آدرس خام ثبت‌شده توسط حسابدار", StringComparison.Ordinal)
            ? address.FullAddress
            : string.Join("\n", new[]
            {
                $"نام گیرنده: {address.ReceiverName}",
                $"موبایل: {address.Mobile}",
                $"کدپستی: {address.PostalCode}",
                $"استان: {address.Province}",
                $"شهر: {address.City}",
                $"نشانی: {address.FullAddress}"
            });
        var payload =
            "🏷 ADDRESS_LABEL_REQUEST\n" +
            $"RequestId: {requestId:N}\n" +
            $"Customer: {identity}\n" +
            "RawAddress:\n" + rawAddress + "\n" +
            "Status: READY_FOR_LABEL";
        var result = await _sender.SendAsync(_options.AddressLabelPrintChatId.Trim(), payload, ct);
        if (!result.IsSuccessful)
            await ReportAddressLabelFailureAsync(requestId, customer, warningChatId,
                $"نسخه چاپ لیبل آدرس ارسال نشد: {result.Error}", ct);
    }

    private async Task ReportAddressLabelFailureAsync(
        Guid requestId, Customer customer, long warningChatId, string error, CancellationToken ct)
    {
        var message =
            "⚠️ خطای لیبل آدرس\n" +
            $"آیدی مشتری: {OrderCustomerLabel(customer)}\n" +
            $"شناسه درخواست: {requestId:N}\n" +
            error;
        await ReplyAsync(warningChatId, message, ct);
        if (!string.IsNullOrWhiteSpace(_options.ShippingChatId) &&
            !string.Equals(_options.ShippingChatId.Trim(), warningChatId.ToString(), StringComparison.Ordinal))
            await _sender.SendAsync(_options.ShippingChatId.Trim(), message, ct);
    }

    private async Task SendShippingTextChunksAsync(
        long chatId, string title, IEnumerable<string> lines, CancellationToken ct)
    {
        const int maximumLength = 3500;
        var chunk = new System.Text.StringBuilder(title + "\n\n");
        foreach (var line in lines)
        {
            if (chunk.Length + line.Length + 2 > maximumLength && chunk.Length > title.Length + 2)
            {
                await ReplyAsync(chatId, chunk.ToString().TrimEnd(), ct);
                chunk.Clear();
                chunk.Append("ادامه گزارش:\n\n");
            }
            chunk.AppendLine(line);
        }
        if (chunk.Length > title.Length + 2)
            await ReplyAsync(chatId, chunk.ToString().TrimEnd(), ct);
    }

    private async Task<bool> IsAuthorizedAccountingShippingAdminAsync(long chatId, long userId, CancellationToken ct) =>
        IsAuthorizedShippingOperator(userId) &&
        await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == chatId.ToString(), ct);

    private bool IsAuthorizedShippingOperator(long userId)
    {
        if (IsPrimaryOwner(userId)) return true;
        return _options.ShippingOperatorUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => long.TryParse(value, out var configuredId) && configuredId == userId);
    }

    private async Task<bool> IsAuthorizedShippingAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        if (IsPrimaryOwner(userId)) return true;
        if (!string.Equals(chatId.ToString(), _options.ShippingChatId.Trim(), StringComparison.Ordinal))
            return false;
        return await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct);
    }
}
