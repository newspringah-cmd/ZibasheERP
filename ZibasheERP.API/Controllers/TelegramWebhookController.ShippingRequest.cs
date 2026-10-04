using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Integrations.TrackTelegramGroupMembership;
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
            var registrarCustomer = await CreateManualAddressRegistrationCustomerAsync(callback.From.Id, ct);
            _orderFlowDrafts.SetShippingPreparation(new TelegramShippingPreparationDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id,
                CustomerId = registrarCustomer.Id,
                Stage = TelegramShippingPreparationStage.AwaitingNewAddress,
                RegistrationOnly = true,
                AllowUnlinkedChat = true
            });
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendShippingInputPromptAsync(callback.Message.Chat.Id, "آدرس کامل را وارد کنید", ct);
            return true;
        }

        if (callback.Data == "shipping:manageconnections")
        {
            if (!IsAuthorizedShippingOperator(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای مدیر و حسابدار فعال است.", ct, true);
                return true;
            }
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendShippingGroupConnectionsAsync(callback.Message.Chat.Id, callback.From.Id, ct);
            return true;
        }

        if (callback.Data.StartsWith("shipping:unlinkconfirm:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:unlinkconfirm:".Length..], "N", out var unlinkCandidateId))
        {
            if (!IsAuthorizedShippingOperator(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای مدیر و حسابدار فعال است.", ct, true);
                return true;
            }
            var candidate = await _db.CustomerTelegramGroups.AsNoTracking()
                .Include(value => value.Customer)
                .FirstOrDefaultAsync(value => value.Id == unlinkCandidateId && !value.IsDeleted && value.IsActive &&
                    value.ChatId == callback.Message.Chat.Id.ToString(), ct);
            if (candidate is null)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این اتصال دیگر فعال نیست.", ct, true);
                return true;
            }
            var activeCount = await _db.CustomerTelegramGroups.AsNoTracking().CountAsync(value =>
                !value.IsDeleted && value.IsActive && value.ChatId == callback.Message.Chat.Id.ToString(), ct);
            if (activeCount <= 1)
            {
                await _sender.AnswerCallbackAsync(callback.Id,
                    "آخرین اتصال گروه را نمی‌توان از این بخش حذف کرد.", ct, true);
                return true;
            }
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(),
                $"⚠️ اتصال {OrderCustomerLabel(candidate.Customer)} از این گروه حذف شود؟\n\n" +
                "مشتری، آدرس‌ها، سفارش‌ها و فاکتورها حذف نمی‌شوند؛ فقط ارتباط این مشتری با همین گروه غیرفعال می‌شود.",
                new IReadOnlyCollection<TelegramInlineButton>[]
                {
                    new[]
                    {
                        new TelegramInlineButton("✅ بله، اتصال اشتباه است", $"shipping:unlinkexecute:{candidate.Id:N}"),
                        new TelegramInlineButton("↩️ انصراف", "shipping:manageconnections")
                    }
                }, ct);
            return true;
        }

        if (callback.Data.StartsWith("shipping:unlinkexecute:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:unlinkexecute:".Length..], "N", out var unlinkId))
        {
            if (!IsAuthorizedShippingOperator(callback.From.Id))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "این گزینه فقط برای مدیر و حسابدار فعال است.", ct, true);
                return true;
            }
            var removed = await RemoveShippingGroupConnectionAsync(
                callback.Message.Chat.Id, unlinkId, callback.From.Id, ct);
            await _sender.AnswerCallbackAsync(callback.Id,
                removed ? "اتصال اشتباه در این گروه غیرفعال شد." : "اتصال تغییر نکرد؛ اطلاعات گروه عوض شده است.", ct, true);
            await StartShippingPreparationAsync(callback.Message.Chat.Id, callback.From.Id, ct);
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
            await SetPrimaryShippingCustomerAsync(
                callback.Message.Chat.Id, preparedCustomerId, callback.From.Id, ct);
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
            await SendShippingInputPromptAsync(
                callback.Message.Chat.Id, "آدرس کامل را وارد کنید", ct);
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

        if (callback.Data.StartsWith("shipping:idreport:", StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data["shipping:idreport:".Length..], "N", out var reportRequestId))
        {
            await SendSingleCustomerShippingReportAsync(callback, reportRequestId, ct);
            return true;
        }

        if (callback.Data == "shipping:trackingdone")
        {
            await _sender.AnswerCallbackAsync(callback.Id, "کد رهگیری قبلاً ارسال شده است ✅", ct, true);
            return true;
        }

        const string customerTrackingPrefix = "shipping:trackingcustomer:";
        if (callback.Data.StartsWith(customerTrackingPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(callback.Data[customerTrackingPrefix.Length..], "N", out var trackingCustomerId))
        {
            if (!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id, callback.From.Id, ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
                return true;
            }
            var customerExists = await _db.Customers.AsNoTracking().AnyAsync(value =>
                !value.IsDeleted && value.Id == trackingCustomerId, ct);
            if (!customerExists)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "اطلاعات مشتری پیدا نشد.", ct, true);
                return true;
            }
            _orderFlowDrafts.SetShippingTrackingPhoto(new TelegramShippingTrackingPhotoDraft
            {
                ChatId = callback.Message.Chat.Id,
                UserId = callback.From.Id,
                CustomerId = trackingCustomerId,
                ShippingRequestId = Guid.Empty,
                SourceMessageId = callback.Message.MessageId,
                HasBatchControls = false
            });
            await _sender.AnswerCallbackAsync(callback.Id, "عکس کد رهگیری را ارسال کنید.", ct, true);
            await ReplyAsync(callback.Message.Chat.Id,
                "📸 عکس کد رهگیری همین مشتری را ارسال کنید. برای لغو، پیام /cancel را بفرستید.", ct);
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
            .Include(value => value.Bottle)
            .Include(value => value.SourceSalesListRequest)
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
        var lines = items.Select((item, index) => FormatShippingItemLine(item, index));
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
                },
                new[] { new TelegramInlineButton("📋 گزارش این آیدی", $"shipping:idreport:{requestId:N}") }
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
        var senderUserId = message.From?.Id ?? 0;
        var isAnonymousReply = message.SenderChat?.Id == message.Chat.Id && message.ReplyToMessage is not null;
        if (!_orderFlowDrafts.TryGetShippingPreparation(
                message.Chat.Id, senderUserId, out var draft, isAnonymousReply))
            return false;
        var isAnonymousAdminReply = draft.Stage == TelegramShippingPreparationStage.AwaitingNewAddress &&
            message.SenderChat?.Id == message.Chat.Id && message.ReplyToMessage is not null;
        var authorized = isAnonymousAdminReply || (message.From is not null &&
            (draft.AllowUnlinkedChat
                ? IsAuthorizedShippingOperator(message.From.Id)
                : await IsAuthorizedAccountingShippingAdminAsync(message.Chat.Id, message.From.Id, ct)));
        if (!authorized)
        {
            return true;
        }
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return true;
        var command = input.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Split('@', 2)[0];
        if (string.Equals(command, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _orderFlowDrafts.ClearShippingPreparation(message.Chat.Id, draft.UserId);
            await ReplyAsync(message.Chat.Id, "عملیات ثبت آدرس و اتصال گروه لغو شد.", ct);
            return true;
        }
        // Button-only stages must not capture ordinary group messages. Keeping the
        // draft is intentional so the operator can still continue with its buttons.
        if (draft.Stage is TelegramShippingPreparationStage.AwaitingAddressChoice or
            TelegramShippingPreparationStage.Ready)
            return false;

        // Commands always belong to the normal bot command pipeline (except /cancel
        // above and /ad, which is handled before this method).
        if (!string.IsNullOrWhiteSpace(command) && command.StartsWith('/'))
            return false;

        // Group input prompts use ForceReply. Only consume a textual response when it
        // is actually replying to the prompt, otherwise normal customer questions in
        // the same group would be mistaken for an address or username.
        if (IsGroup(message.Chat.Type) && message.ReplyToMessage is null)
            return false;

        if (draft.Stage == TelegramShippingPreparationStage.AwaitingIdentity)
        {
            var customer = await ResolveShippingCustomerAsync(input, draft.LinkGroupOnIdentity, ct);
            if (customer is null)
            {
                await ReplyAsync(message.Chat.Id, "مشتری با این آیدی پیدا نشد.", ct);
                return true;
            }
            if (draft.LinkGroupOnIdentity &&
                !await TryLinkShippingGroupAsync(message.Chat, customer, ct))
            {
                await ReplyAsync(message.Chat.Id,
                    "این مشتری یا گروه قبلاً اتصال متفاوتی دارد؛ اتصال خودکار انجام نشد.", ct);
                return true;
            }
            draft.CustomerId = customer.Id;
            draft.Stage = draft.RegistrationOnly
                ? TelegramShippingPreparationStage.AwaitingNewAddress
                : TelegramShippingPreparationStage.AwaitingAddressChoice;
            _orderFlowDrafts.SetShippingPreparation(draft);
            if (draft.RegistrationOnly)
                await SendShippingInputPromptAsync(message.Chat.Id, "آدرس کامل را وارد کنید", ct);
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
                RegistrationTelegramChatId = (draft.RegistrationOnly ? draft.UserId : message.Chat.Id)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsDefault = !hasAddress
            };
            _db.Addresses.Add(address);
            await _db.SaveChangesAsync(ct);
            // Removing the consumed bot prompt also clears its ForceReply for members
            // who open the group after the operator has registered the address.
            if (message.ReplyToMessage is { MessageId: > 0, From.IsBot: true } addressPrompt &&
                addressPrompt.Text?.StartsWith("آدرس کامل را وارد کنید", StringComparison.Ordinal) == true)
            {
                var removed = await _sender.DeleteMessageAsync(
                    message.Chat.Id.ToString(), addressPrompt.MessageId, ct);
                if (!removed.IsSuccessful)
                    _logger.LogWarning("Could not remove consumed shipping address prompt: {Error}", removed.Error);
            }
            if (draft.RegistrationOnly)
            {
                draft.AddressId = address.Id;
                draft.Stage = TelegramShippingPreparationStage.AwaitingDescription;
                _orderFlowDrafts.SetShippingPreparation(draft);
                await SendShippingInputPromptAsync(message.Chat.Id,
                    "توضیحات ارسال را وارد کنید؛ اگر توضیحی ندارید، علامت - را بفرستید.", ct);
                return true;
            }
            draft.AddressId = address.Id;
            draft.Stage = TelegramShippingPreparationStage.Ready;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingPreparationPreviewAsync(draft, ct);
            return true;
        }
        if (draft.Stage == TelegramShippingPreparationStage.AwaitingDescription)
        {
            if (input.Length > 1000)
            {
                await ReplyAsync(message.Chat.Id, "توضیحات حداکثر می‌تواند ۱۰۰۰ کاراکتر باشد.", ct);
                return true;
            }
            if (!draft.AddressId.HasValue)
            {
                _orderFlowDrafts.ClearShippingPreparation(message.Chat.Id, draft.UserId);
                await ReplyAsync(message.Chat.Id, "اطلاعات آدرس پیدا نشد؛ ثبت دستی را دوباره آغاز کنید.", ct);
                return true;
            }
            draft.ShippingNotes = input == "-" ? null : input;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await DispatchManualRegisteredAddressAsync(draft, ct);
            return true;
        }
        return false;
    }

    private async Task<Customer> CreateManualAddressRegistrationCustomerAsync(
        long registrarTelegramUserId, CancellationToken ct)
    {
        var registrationId = Guid.NewGuid();
        var customer = new Customer
        {
            Id = registrationId,
            CreatedAt = DateTime.UtcNow,
            FullName = $"ثبت دستی توسط {registrarTelegramUserId}",
            Mobile = $"MANUAL-{registrationId:N}"[..20],
            Notes = $"manual-address-registration:{registrarTelegramUserId}:{registrationId:N}",
            CanPlaceOrder = false
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    private async Task DispatchManualRegisteredAddressAsync(
        TelegramShippingPreparationDraft draft, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ShippingChatId))
        {
            await ReplyAsync(draft.ChatId,
                "گروه مسئول پست تنظیم نشده است؛ توضیحات ذخیره شد و پس از رفع تنظیمات دوباره آن را ارسال کنید.", ct);
            return;
        }

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.Id == draft.CustomerId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.Id == draft.AddressId, ct);
        if (customer is null || address is null)
        {
            _orderFlowDrafts.ClearShippingPreparation(draft.ChatId, draft.UserId);
            await ReplyAsync(draft.ChatId, "اطلاعات ثبت دستی کامل نیست؛ عملیات را دوباره آغاز کنید.", ct);
            return;
        }

        var notes = string.IsNullOrWhiteSpace(draft.ShippingNotes)
            ? "ندارد"
            : draft.ShippingNotes.Trim();
        var requestId = Guid.NewGuid();
        var shippingMessage =
            $"📦 درخواست ارسال دستی\n\nثبت‌کننده: {draft.UserId}\n\n" +
            $"{FormatAddressForDisplay(address)}\n\n📝 توضیحات:\n{notes}";
        var buttons = new IReadOnlyCollection<TelegramInlineButton>[]
        {
            new[]
            {
                new TelegramInlineButton("📸 ارسال کد رهگیری",
                    $"shipping:trackingcustomer:{customer.Id:N}")
            }
        };
        var sent = await _sender.SendInlineKeyboardAsync(
            _options.ShippingChatId.Trim(), shippingMessage, buttons, ct);
        if (!sent.IsSuccessful)
        {
            await ReplyAsync(draft.ChatId,
                $"ارسال آدرس به گروه آماده‌سازی ناموفق بود: {sent.Error}\nتوضیحات محفوظ است؛ دوباره همان توضیحات را ارسال کنید.", ct);
            return;
        }

        _orderFlowDrafts.ClearShippingPreparation(draft.ChatId, draft.UserId);
        await ReplyAsync(draft.ChatId,
            "آدرس و توضیحات برای آماده‌سازی ارسال شد و نسخه بدون توضیحات برای چاپ لیبل در حال ساخت است ✅", ct);
        await SendAddressLabelCopyAsync(requestId, customer, address, draft.ChatId, ct);
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
            _orderFlowDrafts.SetShippingPreparation(new TelegramShippingPreparationDraft
            {
                ChatId = message.Chat.Id,
                UserId = message.From.Id,
                Stage = TelegramShippingPreparationStage.AwaitingIdentity,
                AllowUnlinkedChat = true,
                LinkGroupOnIdentity = true
            });
            await SendShippingInputPromptAsync(message.Chat.Id,
                "یوزرنیم مشتری را به‌صورت @username وارد کنید. پس از ثبت، این گروه به‌صورت دائمی به مشتری متصل می‌شود.", ct);
            return true;
        }
        await StartShippingPreparationAsync(message.Chat.Id, message.From.Id, ct);
        return true;
    }

    private async Task SendShippingInputPromptAsync(long chatId, string text, CancellationToken ct)
    {
        var prompt = chatId < 0
            ? text + "\n\nلطفاً روی همین پیام Reply بزنید و متن را پیست و ارسال کنید. برای لغو /cancel را بفرستید."
            : text;
        await _sender.SendForceReplyAsync(chatId.ToString(), prompt, ct);
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
            var changed = false;
            foreach (var link in exactLinks)
            {
                var linkChanged = false;
                // An observed message proves the bot is available, but it must not
                // revive an old/manual inactive customer association. Restore only
                // links explicitly suspended because the bot became unavailable.
                var availability = TelegramGroupMembershipPolicy.ApplyAvailability(
                    link.IsActive, link.RestoreOnBotRejoin, canDeliver: true);
                if (link.IsActive != availability.IsActive ||
                    link.RestoreOnBotRejoin != availability.RestoreOnBotRejoin)
                {
                    link.IsActive = availability.IsActive;
                    link.RestoreOnBotRejoin = availability.RestoreOnBotRejoin;
                    link.LastSeenAt = now;
                    linkChanged = true;
                }

                var title = chat.Title?.Trim();
                if (!string.IsNullOrWhiteSpace(title) &&
                    !string.Equals(link.Title, title, StringComparison.Ordinal))
                {
                    link.Title = title;
                    linkChanged = true;
                }

                var username = chat.Username?.Trim().TrimStart('@');
                if (!string.IsNullOrWhiteSpace(username) &&
                    !string.Equals(link.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    link.Username = username;
                    linkChanged = true;
                }

                if (linkChanged)
                {
                    link.UpdatedAt = now;
                    changed = true;
                }
            }

            if (changed)
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Recovered Telegram customer-group mapping after observing a message from chat {TelegramGroupChatId}; mappings={MappingCount}.",
                    chatId,
                    exactLinks.Length);
            }
            return exactLinks.Any(value => value.IsActive);
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

    private async Task<bool> TryLinkShippingGroupAsync(
        TelegramChat chat, Customer customer, CancellationToken ct)
    {
        var chatId = chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var conflictingChat = await _db.CustomerTelegramGroups.FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == chatId && value.CustomerId != customer.Id, ct);
        if (conflictingChat is not null) return false;
        var customerGroup = await _db.CustomerTelegramGroups.FirstOrDefaultAsync(value =>
            !value.IsDeleted && value.CustomerId == customer.Id, ct);
        if (customerGroup is not null && customerGroup.ChatId != chatId) return false;
        var now = DateTime.UtcNow;
        if (customerGroup is null)
        {
            customerGroup = new CustomerTelegramGroup
            {
                Id = Guid.NewGuid(), CustomerId = customer.Id, ChatId = chatId,
                Title = string.IsNullOrWhiteSpace(chat.Title) ? chatId : chat.Title.Trim(),
                Username = chat.Username?.Trim().TrimStart('@'), IsActive = true,
                IsPrimaryForShipping = true,
                LinkedAt = now, LastSeenAt = now, CreatedAt = now
            };
            _db.CustomerTelegramGroups.Add(customerGroup);
        }
        else
        {
            customerGroup.IsActive = true;
            customerGroup.RestoreOnBotRejoin = false;
            customerGroup.IsPrimaryForShipping = true;
            customerGroup.LastSeenAt = now;
            customerGroup.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<Customer?> ResolveShippingCustomerAsync(
        string identity, bool createLegacyCustomer, CancellationToken ct)
    {
        var normalized = identity.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (long.TryParse(normalized, out _))
            return await _db.Customers.FirstOrDefaultAsync(value =>
                !value.IsDeleted && value.TelegramId == normalized, ct);

        var username = normalized.ToLowerInvariant();
        var customer = await _db.Customers.FirstOrDefaultAsync(value => !value.IsDeleted &&
            value.Username != null &&
            (value.Username.ToLower() == username || value.Username.ToLower() == "@" + username), ct);
        if (customer is not null || !createLegacyCustomer) return customer;

        var requestIdentity = await _db.SalesListRequests.AsNoTracking()
            .Where(value => !value.IsDeleted &&
                ((value.TelegramUsername != null &&
                  (value.TelegramUsername.ToLower() == username || value.TelegramUsername.ToLower() == "@" + username)) ||
                 (value.GiftRecipientTelegramUsername != null &&
                  (value.GiftRecipientTelegramUsername.ToLower() == username ||
                   value.GiftRecipientTelegramUsername.ToLower() == "@" + username))))
            .OrderByDescending(value => value.ConfirmedAt ?? value.CreatedAt)
            .Select(value => new
            {
                TelegramId = value.TelegramUsername != null &&
                             (value.TelegramUsername.ToLower() == username ||
                              value.TelegramUsername.ToLower() == "@" + username)
                    ? value.TelegramUserId
                    : value.GiftRecipientTelegramUserId
            })
            .FirstOrDefaultAsync(ct);
        var telegramId = requestIdentity?.TelegramId?.Trim();
        if (long.TryParse(telegramId, out _))
        {
            customer = await _db.Customers.FirstOrDefaultAsync(value =>
                !value.IsDeleted && value.TelegramId == telegramId, ct);
            if (customer is not null)
            {
                customer.Username = normalized;
                customer.UpdatedAt = DateTime.UtcNow;
                return customer;
            }
        }

        var customerId = Guid.NewGuid();
        customer = new Customer
        {
            Id = customerId,
            CreatedAt = DateTime.UtcNow,
            FullName = $"مشتری @{normalized}",
            Mobile = $"TGADDRESS{customerId:N}"[..20],
            TelegramId = long.TryParse(telegramId, out _) ? telegramId : null,
            Username = normalized,
            Notes = requestIdentity is null
                ? "مشتری قدیمی هنگام اتصال گروه از طریق /ad ایجاد شد."
                : "مشتری از سابقه آیتم‌های فروش هنگام اتصال گروه از طریق /ad بازیابی شد."
        };
        _db.Customers.Add(customer);
        return customer;
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
        var primaryLinks = linkedCustomers.Where(value => value.IsPrimaryForShipping).ToArray();
        var selectedLink = linkedCustomers.Length == 1
            ? linkedCustomers[0]
            : primaryLinks.Length == 1 ? primaryLinks[0] : null;
        if (selectedLink is not null)
        {
            draft.CustomerId = selectedLink.CustomerId;
            _orderFlowDrafts.SetShippingPreparation(draft);
            await SendShippingAddressChoicesAsync(draft, ct);
            return;
        }
        await SendShippingGroupConnectionsAsync(chatId, userId, ct);
    }

    private async Task SendShippingGroupConnectionsAsync(long chatId, long userId, CancellationToken ct)
    {
        var links = await _db.CustomerTelegramGroups.AsNoTracking()
            .Where(value => !value.IsDeleted && value.IsActive && value.ChatId == chatId.ToString())
            .Include(value => value.Customer)
            .OrderByDescending(value => value.IsPrimaryForShipping)
            .ThenBy(value => value.Customer.FullName)
            .ToArrayAsync(ct);
        if (links.Length == 0)
        {
            await ReplyAsync(chatId, "این گروه اتصال فعال به مشتری ندارد.", ct);
            return;
        }
        var draft = new TelegramShippingPreparationDraft
        {
            ChatId = chatId,
            UserId = userId,
            Stage = TelegramShippingPreparationStage.AwaitingAddressChoice
        };
        _orderFlowDrafts.SetShippingPreparation(draft);
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        foreach (var link in links)
        {
            var primary = link.IsPrimaryForShipping ? " ⭐" : string.Empty;
            buttons.Add(new[]
            {
                new TelegramInlineButton(
                    $"📮 {OrderCustomerLabel(link.Customer)}{primary}",
                    $"shipping:preparecustomer:{link.CustomerId:N}"),
                new TelegramInlineButton("🗑 حذف اتصال", $"shipping:unlinkconfirm:{link.Id:N}")
            });
        }
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "این گروه به چند مشتری متصل است. مشتری صحیح برای ارسال را انتخاب کنید؛ " +
            "انتخاب شما به‌عنوان مشتری اصلی ارسال ذخیره می‌شود.\n\n" +
            "اتصال نامرتبط را فقط پس از اطمینان با دکمه حذف اتصال پاک کنید.", buttons, ct);
    }

    private async Task SetPrimaryShippingCustomerAsync(
        long chatId, Guid customerId, long operatorUserId, CancellationToken ct)
    {
        var chatIdText = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var links = await _db.CustomerTelegramGroups.Where(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == chatIdText).ToArrayAsync(ct);
        var selected = links.FirstOrDefault(value => value.CustomerId == customerId);
        if (selected is null || (selected.IsPrimaryForShipping && links.Count(value => value.IsPrimaryForShipping) == 1))
            return;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        foreach (var link in links.Where(value => value.IsPrimaryForShipping))
        {
            link.IsPrimaryForShipping = false;
            link.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        selected.IsPrimaryForShipping = true;
        selected.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _logger.LogInformation(
            "Telegram shipping primary changed for group {ChatId} to customer {CustomerId} by operator {OperatorUserId}.",
            chatIdText, customerId, operatorUserId);
    }

    private async Task<bool> RemoveShippingGroupConnectionAsync(
        long chatId, Guid linkId, long operatorUserId, CancellationToken ct)
    {
        var chatIdText = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var links = await _db.CustomerTelegramGroups.Where(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == chatIdText).ToArrayAsync(ct);
        if (links.Length <= 1)
            return false;
        var removed = links.FirstOrDefault(value => value.Id == linkId);
        if (removed is null)
            return false;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        removed.IsPrimaryForShipping = false;
        removed.RestoreOnBotRejoin = false;
        removed.IsActive = false;
        removed.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var remaining = links.Where(value => value.Id != removed.Id).ToArray();
        if (remaining.Length == 1 && !remaining[0].IsPrimaryForShipping)
        {
            remaining[0].IsPrimaryForShipping = true;
            remaining[0].UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        _logger.LogWarning(
            "Telegram customer-group link {LinkId} for customer {CustomerId} was disabled in group {ChatId} by operator {OperatorUserId}.",
            removed.Id, removed.CustomerId, chatIdText, operatorUserId);
        return true;
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
        var hasMultipleConnections = await _db.CustomerTelegramGroups.AsNoTracking().CountAsync(value =>
            !value.IsDeleted && value.IsActive && value.ChatId == draft.ChatId.ToString(), ct) > 1;
        if (hasMultipleConnections)
            buttons.Add(new[] { new TelegramInlineButton("⚙️ مدیریت اتصال‌های این گروه", "shipping:manageconnections") });
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
        foreach (var item in items)
        {
            item.ShippingRequestId = requestId;
            item.ShippingRequestedAt = now;
            item.Order!.DeliveryAddressId = draft.AddressId;
            item.Order.UpdatedAt = now;
            item.UpdatedAt = now;
        }
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
            shippingButtons.Add(new[]
            {
                new TelegramInlineButton("📋 گزارش این آیدی", $"shipping:idreport:{requestId:N}")
            });
        }
        else
        {
            // Legacy address-only requests can still receive and deliver a tracking photo.
            // Guid.Empty prevents unrelated order items from being marked as shipped.
            shippingButtons.Add(new[]
            {
                new TelegramInlineButton("📸 ارسال کد رهگیری",
                    $"shipping:trackingcustomer:{customer.Id:N}")
            });
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
        if (sent.MessageId.HasValue)
        {
            foreach (var item in items) item.ShippingTelegramMessageId = sent.MessageId.Value;
            await _db.SaveChangesAsync(ct);
        }
        _orderFlowDrafts.ClearShippingPreparation(callback.Message.Chat.Id, draft.UserId);
        await _sender.AnswerCallbackAsync(callback.Id,
            "برای گروه پست ارسال شد ✅ لیبل در حال آماده‌سازی است.", ct, true);
        await ReplyAsync(callback.Message.Chat.Id,
            "آدرس جهت آماده سازی و ارسال با موفقیت ثبت شد", ct);
        await SendAddressLabelCopyAsync(requestId, customer, address, callback.Message.Chat.Id, ct);
    }

    private async Task SendSingleCustomerShippingReportAsync(
        TelegramCallbackQuery callback,
        Guid requestId,
        CancellationToken ct)
    {
        var message = callback.Message!;
        if (!await IsAuthorizedShippingAdminAsync(message.Chat.Id, callback.From.Id, ct))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "دسترسی مسئول ارسال ندارید.", ct, true);
            return;
        }

        var items = await _db.OrderItems.AsNoTracking()
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList)
            .Include(value => value.Perfume)
            .Include(value => value.Bottle)
            .Include(value => value.SourceSalesListRequest)
            .Where(value => !value.IsDeleted && value.ShippingRequestId == requestId)
            .OrderBy(value => value.CreatedAt)
            .ToArrayAsync(ct);
        if (items.Length == 0 || items[0].Order?.Customer is null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "عطری برای این درخواست پیدا نشد.", ct, true);
            return;
        }

        var customer = items[0].Order!.Customer;
        var lines = items.Select((item, index) => FormatShippingItemLine(item, index));
        await _sender.AnswerCallbackAsync(callback.Id, "گزارش آماده شد ✅", ct);
        await ReplyAsync(message.Chat.Id,
            $"📋 گزارش {OrderCustomerLabel(customer)}\n\n{string.Join("\n", lines)}", ct);
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
            .Include(value => value.Bottle)
            .Include(value => value.SourceSalesListRequest)
            .Where(value => !value.IsDeleted && value.ShippingRequestId.HasValue &&
                selected.Contains(value.ShippingRequestId.Value) &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip).ToArrayAsync(ct);
        var groups = items.GroupBy(ShippingItemName).OrderBy(value => value.Key).Select(group =>
        {
            var bottleSummary = group
                .GroupBy(item => new
                {
                    item.RequestedVolumeMl,
                    Description = FormatShippingBottleCategory(item)
                })
                .OrderByDescending(value => value.Key.RequestedVolumeMl)
                .ThenBy(value => value.Key.Description)
                .Select(value =>
                    $"  • {value.Count()} عدد {value.Key.RequestedVolumeMl} میل {value.Key.Description}")
                .ToArray();
            var customers = group.GroupBy(value => value.Order!.CustomerId).Select(customerGroup =>
            {
                var specifications = customerGroup
                    .Select(item => $"{item.RequestedVolumeMl} میل: {FormatShippingBottleAndLabel(item)}")
                    .Distinct()
                    .ToArray();
                return $"  • {OrderCustomerLabel(customerGroup.First().Order!.Customer)} — " +
                       $"{customerGroup.Sum(value => value.RequestedVolumeMl)} میل\n" +
                       $"    {string.Join(" | ", specifications)}";
            });
            return $"🧴 {group.Key}\n" +
                   $"تعداد دکانت: {group.Count()} | تعداد نفر: {group.Select(value => value.Order!.CustomerId).Distinct().Count()} | مجموع: {group.Sum(value => value.RequestedVolumeMl)} میل\n" +
                   $"تفکیک شیشه‌ها:\n{string.Join("\n", bottleSummary)}\n" +
                   "مشتری‌ها:\n" +
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
        string[] recipients;
        if (draft.ShippingRequestId == Guid.Empty)
        {
            var registrarChatId = await _db.Addresses.AsNoTracking()
                .Where(value => !value.IsDeleted && value.CustomerId == draft.CustomerId &&
                    value.RegistrationTelegramChatId != null)
                .OrderByDescending(value => value.UpdatedAt ?? value.CreatedAt)
                .Select(value => value.RegistrationTelegramChatId)
                .FirstOrDefaultAsync(ct);
            recipients = string.IsNullOrWhiteSpace(registrarChatId) ? [] : [registrarChatId];
        }
        else
        {
            recipients = await _db.CustomerTelegramGroups.AsNoTracking()
                .Where(value => !value.IsDeleted && value.IsActive && value.CustomerId == draft.CustomerId)
                .Select(value => value.ChatId).Distinct().ToArrayAsync(ct);
        }
        if (customer is null || recipients.Length == 0)
        {
            _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id,
                $"⚠️ مقصد ارسال کد رهگیری برای {(customer is null ? draft.CustomerId.ToString("N") : OrderCustomerLabel(customer))} پیدا نشد.", ct);
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

        var shippedItems = await _db.OrderItems.Include(value => value.Order)
            .Where(value => !value.IsDeleted &&
                value.ShippingRequestId == draft.ShippingRequestId)
            .ToArrayAsync(ct);
        var shippedAt = DateTime.UtcNow;
        foreach (var item in shippedItems.Where(value =>
                     value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped))
        {
            item.FulfillmentStatus = OrderItemFulfillmentStatus.Shipped;
            item.ShippedAt = shippedAt;
            item.UpdatedAt = shippedAt;
        }
        foreach (var order in shippedItems.Select(value => value.Order)
                     .Where(value => value is not null).Distinct()!)
        {
            var hasUnshipped = await _db.OrderItems.AnyAsync(value =>
                !value.IsDeleted && value.OrderId == order!.Id &&
                value.ShippingRequestId != draft.ShippingRequestId &&
                value.FulfillmentStatus != OrderItemFulfillmentStatus.Shipped, ct);
            if (!hasUnshipped)
            {
                order!.Status = OrderStatus.Shipped;
                order.ShippedAt = shippedAt;
                order.UpdatedAt = shippedAt;
            }
        }
        await _db.SaveChangesAsync(ct);

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
            new TelegramInlineButton("✅ ارسال شد", "shipping:trackingdone"),
            new TelegramInlineButton("✅ کد ارسال شد", "shipping:trackingdone")
        });
        var markupResult = await _sender.EditReplyMarkupAsync(
            message.Chat.Id.ToString(), draft.SourceMessageId, updatedButtons, ct);
        if (!markupResult.IsSuccessful)
            await ReplyAsync(message.Chat.Id,
                $"⚠️ عکس کد رهگیری ارسال شد اما وضعیت دکمه بروزرسانی نشد: {markupResult.Error}", ct);
        await ReplyAsync(message.Chat.Id,
            $"✅ عکس کد رهگیری برای {OrderCustomerLabel(customer)} ارسال شد و {shippedItems.Length} دکانت به وضعیت ارسال‌شده تغییر کرد.", ct);
        return true;
    }

    private IQueryable<OrderItem> ReadyShippingItems(Guid customerId) =>
        _db.OrderItems.Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList).Include(value => value.Perfume)
            .Include(value => value.Bottle)
            .Include(value => value.SourceSalesListRequest)
            .Where(value => !value.IsDeleted && value.Order != null && value.Order.CustomerId == customerId &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantedReadyToShip && value.ShippingRequestId == null)
            .OrderBy(value => value.CreatedAt);

    private static string ShippingItemName(OrderItem item) =>
        item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر";

    private static string FormatShippingItemLine(OrderItem item, int index)
        => $"{index + 1}. {ShippingItemName(item)} — {item.RequestedVolumeMl} میل" +
           $" | {FormatShippingBottleAndLabel(item)}";

    private static string FormatShippingBottleAndLabel(OrderItem item)
    {
        var bottle = item.IsBottleOwner
            ? "صاحب باتل"
            : item.Bottle?.Type switch
            {
                BottleType.Fancy => $"فانتزی ({item.Bottle.Name})",
                BottleType.Normal => $"نرمال ({item.Bottle.Name})",
                _ => "نامشخص"
            };
        var label = item.SourceSalesListRequest?.LabelIdentityText?.Trim();
        var labelText = !string.IsNullOrWhiteSpace(label)
            ? $"، لیبل: {label}"
            : item.SourceSalesListRequest?.OmitIdentityOnLabel == true
                ? "، لیبل: بدون آیدی"
                : string.Empty;
        return $"شیشه: {bottle}{labelText}{FormatShippingInventoryMarker(item)}";
    }

    private static string FormatShippingBottleCategory(OrderItem item)
    {
        var bottle = item.IsBottleOwner
            ? "صاحب باتل"
            : item.Bottle?.Type switch
            {
                BottleType.Fancy => "فانتزی",
                BottleType.Normal => "نرمال",
                _ => "با شیشه نامشخص"
            };
        var label = item.SourceSalesListRequest?.LabelIdentityText?.Trim();
        if (!string.IsNullOrWhiteSpace(label))
            bottle += $" — لیبل {label}";
        else if (item.SourceSalesListRequest?.OmitIdentityOnLabel == true)
            bottle += " — لیبل بدون آیدی";
        bottle += FormatShippingInventoryMarker(item);
        return bottle;
    }

    private static string FormatShippingInventoryMarker(OrderItem item) =>
        item.SalesList?.IsInventoryOffer == true || item.Order?.IsInventory == true
            ? " — 🔴 موجودی"
            : string.Empty;

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
