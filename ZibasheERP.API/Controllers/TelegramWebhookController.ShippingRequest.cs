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
            if (!await IsAuthorizedAccountingShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای حسابدار در گروه حسابداری فعال است.", ct, true);
                return true;
            }
            var linkedCustomers = await _db.CustomerTelegramGroups.AsNoTracking()
                .Where(value => !value.IsDeleted && value.IsActive &&
                    value.ChatId == callback.Message.Chat.Id.ToString())
                .Include(value => value.Customer)
                .OrderBy(value => value.Customer.FullName).ToArrayAsync(ct);
            if (linkedCustomers.Length == 0)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گروه هنوز به مشتری متصل نشده است.", ct, true);
                return true;
            }
            var draft = new TelegramShippingPreparationDraft
            {
                ChatId = callback.Message.Chat.Id, UserId = callback.From.Id,
                Stage = TelegramShippingPreparationStage.AwaitingAddressChoice
            };
            _orderFlowDrafts.SetShippingPreparation(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            if (linkedCustomers.Length == 1)
            {
                draft.CustomerId = linkedCustomers[0].CustomerId;
                _orderFlowDrafts.SetShippingPreparation(draft);
                await SendShippingAddressChoicesAsync(draft, ct);
            }
            else
            {
                var buttons = linkedCustomers.Select(value =>
                    (IReadOnlyCollection<TelegramInlineButton>)new[]
                    {
                        new TelegramInlineButton(OrderCustomerLabel(value.Customer),
                            $"shipping:preparecustomer:{value.CustomerId:N}")
                    }).ToArray();
                await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                    "این گروه به چند مشتری متصل است؛ مشتری را انتخاب کنید:", buttons, ct);
            }
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
            await ReplyAsync(callback.Message.Chat.Id,
                "آدرس جدید را در یک پیام و با ۶ بخش بفرستید:\nنام گیرنده | موبایل | استان | شهر | کدپستی | آدرس کامل", ct);
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
                new[] { new TelegramInlineButton("🚚 ارسال شد", $"shipping:sent:{requestId:N}") }
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
        await SendAddressLabelCopyAsync(requestId, customer, address, callback.Message!.Chat.Id, ct);
        await _sender.AnswerCallbackAsync(callback.Id, "درخواست ارسال ثبت شد ✅", ct, true);
        await ReplyAsync(callback.Message!.Chat.Id,
            $"درخواست ارسال {items.Length} آیتم آماده برای مسئول ارسال ثبت شد ✅", ct);
    }

    private async Task<bool> TryHandleShippingPreparationMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null ||
            !_orderFlowDrafts.TryGetShippingPreparation(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedAccountingShippingAdminAsync(message.Chat.Id, message.From.Id, ct))
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
            draft.Stage = TelegramShippingPreparationStage.AwaitingAddressChoice;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingAddressChoicesAsync(draft, ct);
            return true;
        }
        if (draft.Stage == TelegramShippingPreparationStage.AwaitingNewAddress)
        {
            var fields = input.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 6 || fields.Any(string.IsNullOrWhiteSpace) ||
                new string(fields[4].Where(char.IsDigit).ToArray()).Length != 10)
            {
                await ReplyAsync(message.Chat.Id,
                    "فرمت معتبر نیست. دقیقاً بفرستید:\nنام گیرنده | موبایل | استان | شهر | کدپستی ۱۰ رقمی | آدرس کامل", ct);
                return true;
            }
            var hasAddress = await _db.Addresses.AnyAsync(value => !value.IsDeleted && value.CustomerId == draft.CustomerId, ct);
            var address = new Address
            {
                Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, CustomerId = draft.CustomerId,
                ReceiverName = fields[0], Mobile = fields[1], Province = fields[2], City = fields[3],
                PostalCode = new string(fields[4].Where(char.IsDigit).ToArray()), FullAddress = fields[5],
                Description = "ثبت حسابدار", IsDefault = !hasAddress
            };
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync(ct);
            draft.AddressId = address.Id;
            draft.Stage = TelegramShippingPreparationStage.Ready;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingPreparationPreviewAsync(draft, ct);
            return true;
        }
        await ReplyAsync(message.Chat.Id, "از دکمه‌های فرایند ارسال استفاده کنید.", ct);
        return true;
    }

    private async Task SendShippingAddressChoicesAsync(TelegramShippingPreparationDraft draft, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var addresses = await _db.Addresses.AsNoTracking().Where(value => !value.IsDeleted && value.CustomerId == draft.CustomerId)
            .OrderByDescending(value => value.IsDefault).ThenByDescending(value => value.CreatedAt).Take(10).ToArrayAsync(ct);
        var readyCount = await _db.OrderItems.CountAsync(value => !value.IsDeleted && value.Order != null &&
            value.Order.CustomerId == draft.CustomerId &&
            value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && value.ShippingRequestId == null, ct);
        var buttons = addresses.Select(address => (IReadOnlyCollection<TelegramInlineButton>)new[]
        {
            new TelegramInlineButton($"{address.ReceiverName} — {address.City}{(address.IsDefault ? " ✅" : "")}",
                $"shipping:prepareaddress:{address.Id:N}")
        }).ToList();
        buttons.Add(new[] { new TelegramInlineButton("➕ ثبت آدرس جدید", "shipping:preparenew") });
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"📮 آماده‌سازی پست\nمشتری: {OrderCustomerLabel(customer)}\nدکانت آماده و بدون درخواست: {readyCount}\n\nآدرس را بررسی و انتخاب کنید:", buttons, ct);
    }

    private async Task SendShippingPreparationPreviewAsync(TelegramShippingPreparationDraft draft, CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstAsync(value => value.Id == draft.AddressId, ct);
        var items = await ReadyShippingItems(draft.CustomerId).ToArrayAsync(ct);
        var lines = items.Select((item, index) =>
            $"{index + 1}. {ShippingItemName(item)} — {item.RequestedVolumeMl} میل");
        await SendShippingTextChunksAsync(draft.ChatId, "اقلام آماده ارسال:", lines, ct);
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            $"پیش‌نمایش ارسال برای پست\n\nمشتری: {OrderCustomerLabel(customer)}\n" +
            $"گیرنده: {address.ReceiverName}\nموبایل: {address.Mobile}\nکدپستی: {address.PostalCode}\n" +
            $"آدرس: {address.Province}، {address.City}، {address.FullAddress}\n\n" +
            $"تعداد دکانت آماده: {items.Length}\nمجموع میل: {items.Sum(value => value.RequestedVolumeMl)}",
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
        var customer = await _db.Customers.AsNoTracking().FirstAsync(value => value.Id == draft.CustomerId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstAsync(value => value.Id == draft.AddressId, ct);
        var items = await ReadyShippingItems(draft.CustomerId).ToArrayAsync(ct);
        if (items.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دکانت آماده‌ای برای ارسال باقی نمانده است.", ct, true);
            return;
        }
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var item in items) { item.ShippingRequestId = requestId; item.ShippingRequestedAt = now; item.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        var identity = OrderCustomerLabel(customer);
        var lines = items.Select((item, index) =>
            $"{index + 1}. {ShippingItemName(item)} — {item.RequestedVolumeMl} میل — {identity}");
        var sent = await _sender.SendInlineKeyboardAsync(_options.ShippingChatId.Trim(),
            $"📦 درخواست ارسال جدید\n\nآیدی: {identity}\nگیرنده: {address.ReceiverName}\nموبایل: {address.Mobile}\n" +
            $"کدپستی: {address.PostalCode}\nآدرس: {address.Province}، {address.City}، {address.FullAddress}\n\n" +
            $"تعداد دکانت آماده: {items.Length}\nمجموع دکانت قابل ارسال: {items.Sum(value => value.RequestedVolumeMl)} میل",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("☑️ انتخاب برای گزارش کلی", $"shipping:select:{requestId:N}") },
                new[] { new TelegramInlineButton("📊 گزارش کلی انتخاب‌ها", "shipping:batchreport") },
                new[] { new TelegramInlineButton("🚚 ارسال شد", $"shipping:sent:{requestId:N}") }
            }, ct);
        if (!sent.IsSuccessful)
        {
            foreach (var item in items) { item.ShippingRequestId = null; item.ShippingRequestedAt = null; }
            await _db.SaveChangesAsync(ct);
            await _sender.AnswerCallbackAsync(callback.Id, $"ارسال ناموفق بود: {sent.Error}", ct, true);
            return;
        }
        await SendAddressLabelCopyAsync(requestId, customer, address, callback.Message.Chat.Id, ct);
        await SendShippingTextChunksAsync(long.Parse(_options.ShippingChatId.Trim()),
            $"جزئیات دکانت‌های {identity}:", lines, ct);
        _orderFlowDrafts.ClearShippingPreparation(callback.Message.Chat.Id, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, "برای گروه پست ارسال شد ✅", ct, true);
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

    private IQueryable<OrderItem> ReadyShippingItems(Guid customerId) =>
        _db.OrderItems.Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList).Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.CustomerId == customerId &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && value.ShippingRequestId == null)
            .OrderBy(value => value.CreatedAt);

    private static string ShippingItemName(OrderItem item) =>
        item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر";

    private async Task SendAddressLabelCopyAsync(
        Guid requestId, Customer customer, Address address, long warningChatId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AddressLabelPrintChatId)) return;
        var identity = OrderCustomerLabel(customer);
        var payload =
            "🏷 ADDRESS_LABEL_REQUEST\n" +
            $"RequestId: {requestId:N}\n" +
            $"Customer: {identity}\n" +
            $"ReceiverName: {address.ReceiverName}\n" +
            $"Mobile: {address.Mobile}\n" +
            $"PostalCode: {address.PostalCode}\n" +
            $"Province: {address.Province}\n" +
            $"City: {address.City}\n" +
            $"FullAddress: {address.FullAddress}\n" +
            "Status: READY_FOR_LABEL";
        var result = await _sender.SendAsync(_options.AddressLabelPrintChatId.Trim(), payload, ct);
        if (!result.IsSuccessful)
            await ReplyAsync(warningChatId,
                $"⚠️ درخواست پست ثبت شد اما نسخه چاپ لیبل آدرس ارسال نشد: {result.Error}", ct);
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
        await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct) &&
        await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == chatId.ToString(), ct);

    private async Task<bool> IsAuthorizedShippingAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        if (IsPrimaryOwner(userId)) return true;
        if (!string.Equals(chatId.ToString(), _options.ShippingChatId.Trim(), StringComparison.Ordinal))
            return false;
        return await _sender.IsChatAdministratorAsync(chatId.ToString(), userId.ToString(), ct);
    }
}
