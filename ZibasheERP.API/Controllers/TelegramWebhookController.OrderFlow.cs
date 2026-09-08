using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Orders.AdvanceFulfillment;
using ZibasheERP.Application.Features.Shipments.CreateShipment;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static readonly OrderStatus[] OperationalOrderStatuses =
    [
        OrderStatus.Registered, OrderStatus.ListCompleted, OrderStatus.PerfumePurchased,
        OrderStatus.Invoiced, OrderStatus.Paid, OrderStatus.Decanted,
        OrderStatus.ReadyToShip, OrderStatus.Shipped
    ];

    private async Task HandleOrderFlowCallbackAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data!;
        var chatId = callback.Message!.Chat.Id;
        if (data == "orderflow:dashboard")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendOrderFlowDashboardAsync(chatId, ct);
            return;
        }

        if (data == "orderflow:arrival")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendArrivalSelectionAsync(chatId, callback.From.Id, 0, ct);
            return;
        }
        if (data.StartsWith("orderflow:arrivalpage:", StringComparison.Ordinal) &&
            int.TryParse(data["orderflow:arrivalpage:".Length..], out var arrivalPage))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendArrivalSelectionAsync(chatId, callback.From.Id, Math.Max(0, arrivalPage), ct);
            return;
        }
        if (data is "orderflow:completed:purchased" or "orderflow:completed:invoiced")
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendAutomaticallyCompletedStageAsync(chatId,
                data.EndsWith(":purchased", StringComparison.Ordinal) ? "✅ خرید شده" : "✅ فاکتور شده", ct);
            return;
        }
        if (data.StartsWith("orderflow:arrivaltoggle:", StringComparison.Ordinal) &&
            TryParseArrivalToggle(data["orderflow:arrivaltoggle:".Length..], out var arrivalListId, out var togglePage))
        {
            var selected = _orderFlowDrafts.GetArrivalSelection(chatId, callback.From.Id);
            if (!selected.Add(arrivalListId)) selected.Remove(arrivalListId);
            await _sender.AnswerCallbackAsync(callback.Id, "انتخاب بروزرسانی شد.", ct);
            await SendArrivalSelectionAsync(chatId, callback.From.Id, togglePage, ct);
            return;
        }
        if (data == "orderflow:arrivalconfirm")
        {
            await ConfirmArrivedSalesListsAsync(callback, ct);
            return;
        }
        if (data.StartsWith("orderflow:summary:", StringComparison.Ordinal))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendSalesListStageSummaryAsync(chatId, data["orderflow:summary:".Length..], ct);
            return;
        }
        if (data.StartsWith("orderflow:itemstatus:", StringComparison.Ordinal) &&
            int.TryParse(data["orderflow:itemstatus:".Length..], out var itemStatusValue) &&
            Enum.IsDefined(typeof(OrderItemFulfillmentStatus), itemStatusValue))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendOrderItemStageSummaryAsync(chatId, (OrderItemFulfillmentStatus)itemStatusValue, ct);
            return;
        }

        // Legacy per-order transitions are intentionally disabled. Fulfillment is now
        // managed per sales list and per item so payment never controls operational status.
        await _sender.AnswerCallbackAsync(callback.Id, "این مسیر قدیمی غیرفعال شده است؛ از منوی وضعیت سفارش‌ها استفاده کنید.", ct, true);
        return;

#pragma warning disable CS0162
        var parts = data.Split(':');
        if (parts.Length >= 3 && parts[1] == "list" &&
            int.TryParse(parts[2], out var statusValue) && Enum.IsDefined(typeof(OrderStatus), statusValue))
        {
            var page = parts.Length >= 4 && int.TryParse(parts[3], out var parsedPage) ? Math.Max(0, parsedPage) : 0;
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendOrderFlowListAsync(chatId, (OrderStatus)statusValue, page, ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "view" &&
            Guid.TryParseExact(parts[2], "N", out var orderId))
        {
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await SendOrderFlowDetailAsync(chatId, orderId, ct);
            return;
        }

        if (parts.Length == 4 && parts[1] == "advance" &&
            Guid.TryParseExact(parts[2], "N", out orderId) && int.TryParse(parts[3], out statusValue) &&
            Enum.IsDefined(typeof(OrderStatus), statusValue))
        {
            try
            {
                var result = await _mediator.Send(
                    new AdvanceFulfillmentCommand(orderId, (OrderStatus)statusValue), ct);
                await _sender.AnswerCallbackAsync(callback.Id, "وضعیت بروزرسانی شد ✅", ct, true);
                await SendOrderFlowDetailAsync(chatId, orderId, ct);
            }
            catch (Exception exception) when (exception is InvalidOperationException or DbUpdateConcurrencyException)
            {
                await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
            }
            return;
        }

        if (parts.Length == 3 && parts[1] == "ship" && Guid.TryParseExact(parts[2], "N", out orderId))
        {
            await StartOrderShippingAsync(callback, orderId, ct);
            return;
        }

        if (parts.Length == 3 && parts[1] == "address" && Guid.TryParseExact(parts[2], "N", out var addressId))
        {
            if (!_orderFlowDrafts.TryGet(chatId, callback.From.Id, out var draft) ||
                draft.Stage != TelegramOrderShippingStage.AwaitingAddress)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "فرایند منقضی شده است.", ct, true);
                return;
            }
            var validAddress = await _db.Addresses.AsNoTracking().AnyAsync(address =>
                address.Id == addressId && !address.IsDeleted &&
                _db.Orders.Any(order => order.Id == draft.OrderId && order.CustomerId == address.CustomerId), ct);
            if (!validAddress)
            {
                await _sender.AnswerCallbackAsync(callback.Id, "آدرس متعلق به این سفارش نیست.", ct, true);
                return;
            }
            draft.AddressId = addressId;
            draft.Stage = TelegramOrderShippingStage.AwaitingCompany;
            _orderFlowDrafts.Set(draft);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
            await ReplyAsync(chatId, "نام شرکت یا روش ارسال را وارد کنید؛ مثال: پست پیشتاز", ct);
            return;
        }

        if (data == "orderflow:shipconfirm")
        {
            await ConfirmOrderShippingAsync(callback, ct);
            return;
        }
        if (data == "orderflow:shipcancel")
        {
            _orderFlowDrafts.Remove(chatId, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "ثبت ارسال لغو شد.", ct);
            await SendOrderFlowDashboardAsync(chatId, ct);
            return;
        }

        await _sender.AnswerCallbackAsync(callback.Id, "گزینه نامعتبر است.", ct);
#pragma warning restore CS0162
    }

    private async Task SendOrderFlowDashboardAsync(long chatId, CancellationToken ct)
    {
        var listCounts = await _db.SalesLists.AsNoTracking().Where(value => !value.IsDeleted)
            .GroupBy(value => value.Status).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(value => value.Key, value => value.Count, ct);
        var itemCounts = await _db.OrderItems.AsNoTracking().Where(value => !value.IsDeleted)
            .GroupBy(value => value.FulfillmentStatus).Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(value => value.Key, value => value.Count, ct);
        var waitingArrivalLists = await _db.OrderItems.AsNoTracking()
            .Where(value => !value.IsDeleted && value.SalesListId.HasValue &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.WaitingForArrivalInIran)
            .Select(value => value.SalesListId).Distinct().CountAsync(ct);
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>
        {
            new[] { new TelegramInlineButton($"📝 انتظار تکمیل لیست ({listCounts.GetValueOrDefault(SalesListStatus.Open)})", "orderflow:summary:open") },
            new[] { new TelegramInlineButton($"✅ لیست تکمیل‌شده ({listCounts.GetValueOrDefault(SalesListStatus.Full)})", "orderflow:summary:full") },
            new[] { new TelegramInlineButton($"🛒 در انتظار خرید ({listCounts.GetValueOrDefault(SalesListStatus.AwaitingAvailability)})", "orderflow:summary:awaiting") },
            new[] { new TelegramInlineButton($"✅ خرید شده ({waitingArrivalLists})", "orderflow:completed:purchased") },
            new[] { new TelegramInlineButton($"✅ فاکتور شده ({waitingArrivalLists})", "orderflow:completed:invoiced") },
            new[] { new TelegramInlineButton($"✈️ منتظر رسیدن به ایران ({waitingArrivalLists})", "orderflow:arrival") },
            new[] { new TelegramInlineButton($"🧴 صف دکانت ({itemCounts.GetValueOrDefault(OrderItemFulfillmentStatus.DecantQueue)})", "orderflow:itemstatus:8") },
            new[] { new TelegramInlineButton($"📦 آماده ارسال ({itemCounts.GetValueOrDefault(OrderItemFulfillmentStatus.DecantedReadyToShip)})", "orderflow:itemstatus:9") },
            new[] { new TelegramInlineButton($"🚚 ارسال‌شده ({itemCounts.GetValueOrDefault(OrderItemFulfillmentStatus.Shipped)})", "orderflow:itemstatus:10") }
        };
        buttons.Add(new[] { new TelegramInlineButton("↩️ بازگشت به منوی اصلی", "invoiceadmin:menu:main") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            "📦 وضعیت سفارش‌ها از ثبت تا ارسال\n\nوضعیت پرداخت مستقل است و از دکمه‌های زیر فاکتور مدیریت می‌شود.",
            buttons, ct);
    }

    private async Task SendArrivalSelectionAsync(long chatId, long userId, int page, CancellationToken ct)
    {
        const int pageSize = 50;
        var selected = _orderFlowDrafts.GetArrivalSelection(chatId, userId);
        var query = _db.SalesLists.AsNoTracking()
            .Where(list => !list.IsDeleted && _db.OrderItems.Any(item => !item.IsDeleted &&
                item.SalesListId == list.Id &&
                item.FulfillmentStatus == OrderItemFulfillmentStatus.WaitingForArrivalInIran))
            .OrderBy(list => list.PersianName).ThenBy(list => list.PublicCode);
        var total = await query.CountAsync(ct);
        var maxPage = total == 0 ? 0 : (total - 1) / pageSize;
        page = Math.Min(page, maxPage);
        var validIds = await query.Select(list => list.Id).ToArrayAsync(ct);
        selected.RemoveWhere(id => !validIds.Contains(id));
        var lists = await query.Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        var buttons = lists.Select(list =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton($"{(selected.Contains(list.Id) ? "✅" : "⬜")} " +
                    $"{(string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName)}",
                    $"orderflow:arrivaltoggle:{list.Id:N}:{page}")
            }).ToList();
        var navigation = new List<TelegramInlineButton>();
        if (page > 0)
            navigation.Add(new TelegramInlineButton("◀️ ۵۰ عطر قبلی", $"orderflow:arrivalpage:{page - 1}"));
        if (page < maxPage)
            navigation.Add(new TelegramInlineButton("۵۰ عطر بعدی ▶️", $"orderflow:arrivalpage:{page + 1}"));
        if (navigation.Count > 0) buttons.Add(navigation);
        if (selected.Count > 0)
            buttons.Add(new[] { new TelegramInlineButton($"🇮🇷 رسید و ارسال {selected.Count} عطر به صف دکانت", "orderflow:arrivalconfirm") });
        buttons.Add(new[] { new TelegramInlineButton("↩️ وضعیت سفارش‌ها", "orderflow:dashboard") });
        var message = total == 0
            ? "عطر فاکتور‌شده‌ای در انتظار رسیدن به ایران نیست."
            : $"✈️ عطرهای فاکتور‌شده و منتظر رسیدن به ایران: {total}\n" +
              $"صفحه {page + 1} از {maxPage + 1} — انتخاب‌شده: {selected.Count}\n\n" +
              "عطرها را تیک بزنید؛ انتخاب‌ها در صفحات دیگر حفظ می‌شوند.";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), message, buttons, ct);
    }

    private static bool TryParseArrivalToggle(string value, out Guid listId, out int page)
    {
        listId = Guid.Empty;
        page = 0;
        var parts = value.Split(':', 2);
        return parts.Length >= 1 && Guid.TryParseExact(parts[0], "N", out listId) &&
            (parts.Length == 1 || int.TryParse(parts[1], out page)) && page >= 0;
    }

    private async Task SendAutomaticallyCompletedStageAsync(long chatId, string title, CancellationToken ct)
    {
        var lists = await _db.SalesLists.AsNoTracking()
            .Where(list => !list.IsDeleted && _db.OrderItems.Any(item => !item.IsDeleted &&
                item.SalesListId == list.Id &&
                item.FulfillmentStatus >= OrderItemFulfillmentStatus.WaitingForArrivalInIran))
            .OrderByDescending(list => list.UpdatedAt ?? list.CreatedAt).Take(50).ToArrayAsync(ct);
        var lines = lists.Select((list, index) =>
            $"{index + 1}. {(string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName)} — کد {list.PublicCode}");
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            lists.Length == 0 ? $"{title}\n\nموردی وجود ندارد." :
                $"{title}\nآخرین {lists.Length} عطر\n\n{string.Join("\n", lines)}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("↩️ وضعیت سفارش‌ها", "orderflow:dashboard") }
            }, ct);
    }

    private async Task SendSalesListStageSummaryAsync(long chatId, string stage, CancellationToken ct)
    {
        var statuses = stage switch
        {
            "open" => new[] { SalesListStatus.Open },
            "full" => new[] { SalesListStatus.Full },
            "awaiting" => new[] { SalesListStatus.AwaitingAvailability },
            _ => Array.Empty<SalesListStatus>()
        };
        if (statuses.Length == 0)
        {
            await SendOrderFlowDashboardAsync(chatId, ct);
            return;
        }
        var lists = await _db.SalesLists.AsNoTracking()
            .Where(value => !value.IsDeleted && statuses.Contains(value.Status))
            .OrderBy(value => value.PersianName).ThenBy(value => value.PublicCode)
            .Take(50).ToArrayAsync(ct);
        var title = stage switch
        {
            "open" => "📝 آیتم ثبت‌شده / انتظار تکمیل لیست",
            "full" => "✅ لیست تکمیل‌شده",
            _ => "🛒 در انتظار خرید عطر"
        };
        var lines = lists.Select((list, index) =>
            $"{index + 1}. {(string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName)} — کد {list.PublicCode}");
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            lists.Length == 0 ? $"{title}\n\nموردی وجود ندارد." :
                $"{title}\nتعداد نمایش‌داده‌شده: {lists.Length}\n\n{string.Join("\n", lines)}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("↩️ وضعیت سفارش‌ها", "orderflow:dashboard") }
            }, ct);
    }

    private async Task SendOrderItemStageSummaryAsync(
        long chatId, OrderItemFulfillmentStatus status, CancellationToken ct)
    {
        var items = await _db.OrderItems.AsNoTracking()
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.SalesList)
            .Include(value => value.Perfume)
            .Where(value => !value.IsDeleted && value.FulfillmentStatus == status)
            .OrderBy(value => value.UpdatedAt ?? value.CreatedAt).Take(50).ToArrayAsync(ct);
        var lines = items.Select((item, index) =>
            $"{index + 1}. {OrderCustomerLabel(item.Order?.Customer)} — " +
            $"{item.SalesList?.PersianName ?? item.Perfume?.Name ?? item.ManualDescription ?? "عطر"} — {item.RequestedVolumeMl} میل");
        var title = status switch
        {
            OrderItemFulfillmentStatus.DecantQueue => "🧴 صف دکانت",
            OrderItemFulfillmentStatus.DecantedReadyToShip => "📦 دکانت‌شده و آماده ارسال",
            OrderItemFulfillmentStatus.Shipped => "🚚 ارسال‌شده",
            _ => OrderItemFulfillmentStatusLabel(status)
        };
        await _sender.SendInlineKeyboardAsync(chatId.ToString(),
            items.Length == 0 ? $"{title}\n\nموردی وجود ندارد." :
                $"{title}\nتعداد نمایش‌داده‌شده: {items.Length}\n\n{string.Join("\n", lines)}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("↩️ وضعیت سفارش‌ها", "orderflow:dashboard") }
            }, ct);
    }

    private async Task ConfirmArrivedSalesListsAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        var selected = _orderFlowDrafts.GetArrivalSelection(chatId, callback.From.Id).ToArray();
        if (selected.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "حداقل یک عطر را انتخاب کنید.", ct, true);
            return;
        }
        var lists = await _db.SalesLists.AsNoTracking().Where(value => selected.Contains(value.Id) && !value.IsDeleted)
            .OrderBy(value => value.PublicCode).ToArrayAsync(ct);
        if (lists.Length != selected.Length)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "حداقل یک لیست دیگر معتبر نیست؛ دوباره انتخاب کنید.", ct, true);
            return;
        }
        var now = DateTime.UtcNow;
        var affected = await _db.OrderItems.Where(value => !value.IsDeleted && value.SalesListId.HasValue &&
                selected.Contains(value.SalesListId.Value) &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.WaitingForArrivalInIran)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.FulfillmentStatus, OrderItemFulfillmentStatus.DecantQueue)
                .SetProperty(value => value.ArrivedInIranAt, now)
                .SetProperty(value => value.EnteredDecantQueueAt, now)
                .SetProperty(value => value.UpdatedAt, now), ct);
        var failures = new List<string>();
        foreach (var list in lists)
        {
            var result = await SendDecantQueueListAsync(list, ct);
            if (!result.IsSuccessful) failures.Add($"{list.PublicCode}: {result.Error}");
        }
        _orderFlowDrafts.ClearArrivalSelection(chatId, callback.From.Id);
        await _sender.AnswerCallbackAsync(callback.Id, $"{affected} آیتم وارد صف دکانت شد ✅", ct, true);
        await ReplyAsync(chatId,
            failures.Count == 0
                ? $"✅ {lists.Length} عطر به صف دکانت ارسال شد."
                : $"⚠️ وضعیت ثبت شد اما ارسال {failures.Count} پیام صف ناموفق بود:\n{string.Join("\n", failures)}", ct);
        await SendOrderFlowDashboardAsync(chatId, ct);
    }

    private async Task<TelegramSendResult> SendDecantQueueListAsync(SalesList list, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.DecantChatId))
            return new TelegramSendResult(false, "گروه دکانت تنظیم نشده است.");
        var items = await _db.OrderItems.AsNoTracking()
            .Include(value => value.Order).ThenInclude(value => value!.Customer)
            .Include(value => value.Bottle)
            .Where(value => !value.IsDeleted && value.SalesListId == list.Id &&
                value.FulfillmentStatus == OrderItemFulfillmentStatus.DecantQueue)
            .OrderBy(value => value.RowNumber).ThenBy(value => value.CreatedAt).ToArrayAsync(ct);
        var rows = items.Select((item, index) =>
            $"{index + 1}. {OrderCustomerLabel(item.Order?.Customer)} — {item.RequestedVolumeMl} میل — " +
            $"{(item.IsBottleOwner ? "صاحب باتل" : item.Bottle?.Name ?? "شیشه ثبت‌نشده")}");
        var name = string.IsNullOrWhiteSpace(list.PersianName) ? list.EnglishName : list.PersianName;
        return await _sender.SendInlineKeyboardAsync(_options.DecantChatId.Trim(),
            $"🧴 دکانت جدید\n\nعطر: {name}\nکد لیست: {list.PublicCode}\nتعداد آیتم: {items.Length}\n\n{string.Join("\n", rows)}",
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("📸 ارسال عکس دکانت", $"decantphoto:list:{list.Id:N}") }
            }, ct);
    }

    private async Task SendOrderFlowListAsync(long chatId, OrderStatus status, int page, CancellationToken ct)
    {
        const int pageSize = 10;
        var query = _db.Orders.AsNoTracking().Where(order => !order.IsDeleted && order.Status == status);
        var total = await query.CountAsync(ct);
        var maxPage = total == 0 ? 0 : (total - 1) / pageSize;
        page = Math.Min(page, maxPage);
        var orders = await query.Include(order => order.Customer)
            .OrderBy(order => order.RegisteredAt).ThenBy(order => order.Id)
            .Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        var buttons = orders.Select(order =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{OrderCustomerLabel(order.Customer)} • {OrderNumberSuffix(order.OrderNumber)} • {order.FinalAmount:N0}",
                    $"orderflow:view:{order.Id:N}")
            }).ToList();
        var navigation = new List<TelegramInlineButton>();
        if (page > 0) navigation.Add(new TelegramInlineButton("◀️ قبلی", $"orderflow:list:{(int)status}:{page - 1}"));
        if (page < maxPage) navigation.Add(new TelegramInlineButton("بعدی ▶️", $"orderflow:list:{(int)status}:{page + 1}"));
        if (navigation.Count > 0) buttons.Add(navigation);
        buttons.Add(new[] { new TelegramInlineButton("↩️ وضعیت سفارش‌ها", "orderflow:dashboard") });
        var body = total == 0
            ? $"هیچ سفارشی در وضعیت «{OrderStatusLabel(status)}» نیست."
            : $"{OrderStatusLabel(status)} — تعداد: {total}\nصفحه {page + 1} از {maxPage + 1}\n\nبرای مشاهده جزئیات، سفارش را انتخاب کنید.";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), body, buttons, ct);
    }

    private async Task SendOrderFlowDetailAsync(long chatId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders.AsNoTracking()
            .Include(value => value.Customer)
            .Include(value => value.DeliveryAddress)
            .Include(value => value.Items.Where(item => !item.IsDeleted)).ThenInclude(item => item.Perfume)
            .FirstOrDefaultAsync(value => value.Id == orderId && !value.IsDeleted, ct);
        if (order is null)
        {
            await ReplyAsync(chatId, "سفارش پیدا نشد.", ct);
            return;
        }
        var items = string.Join("\n", order.Items.OrderBy(item => item.RowNumber).Select(item =>
            $"• {item.Perfume?.Name ?? item.ManualDescription ?? "آیتم"} — {item.RequestedVolumeMl} میل"));
        var address = order.DeliveryAddress is null ? "ثبت/انتخاب نشده" :
            $"{order.DeliveryAddress.ReceiverName} — {order.DeliveryAddress.City}";
        var text = $"📦 سفارش {order.OrderNumber}\n" +
                   $"مشتری: {OrderCustomerLabel(order.Customer)}\n" +
                   $"وضعیت: {OrderStatusLabel(order.Status)}\n" +
                   $"مبلغ: {order.FinalAmount:N0} تومان\n" +
                   $"آدرس: {address}\n\n{items}";
        var buttons = new List<IReadOnlyCollection<TelegramInlineButton>>();
        if (order.Status == OrderStatus.Paid)
            buttons.Add(new[] { new TelegramInlineButton("✅ ثبت دکانت‌شدن", $"orderflow:advance:{order.Id:N}:{(int)OrderStatus.Decanted}") });
        else if (order.Status == OrderStatus.Decanted)
            buttons.Add(new[] { new TelegramInlineButton("📦 ثبت آماده ارسال", $"orderflow:advance:{order.Id:N}:{(int)OrderStatus.ReadyToShip}") });
        else if (order.Status == OrderStatus.ReadyToShip)
            buttons.Add(new[] { new TelegramInlineButton("🚚 ثبت مرسوله و ارسال", $"orderflow:ship:{order.Id:N}") });
        buttons.Add(new[] { new TelegramInlineButton("↩️ بازگشت به این مرحله", $"orderflow:list:{(int)order.Status}:0") });
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), text, buttons, ct);
    }

    private async Task StartOrderShippingAsync(TelegramCallbackQuery callback, Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(value => value.Id == orderId && !value.IsDeleted, ct);
        if (order is null || order.Status != OrderStatus.ReadyToShip)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "فقط سفارش آماده ارسال قابل ثبت است.", ct, true);
            return;
        }
        var addresses = await _db.Addresses.AsNoTracking()
            .Where(value => value.CustomerId == order.CustomerId && !value.IsDeleted)
            .OrderByDescending(value => value.Id == order.DeliveryAddressId).ThenByDescending(value => value.IsDefault)
            .ToArrayAsync(ct);
        if (addresses.Length == 0)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "مشتری آدرس فعالی ندارد.", ct, true);
            return;
        }
        var draft = new TelegramOrderShippingDraft
        {
            ChatId = callback.Message!.Chat.Id, UserId = callback.From.Id, OrderId = order.Id,
            Stage = TelegramOrderShippingStage.AwaitingAddress
        };
        _orderFlowDrafts.Set(draft);
        var buttons = addresses.Take(10).Select(address =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton($"{address.ReceiverName} — {address.City}{(address.IsDefault ? " ✅" : "")}",
                    $"orderflow:address:{address.Id:N}")
            }).Append(new[] { new TelegramInlineButton("❌ لغو", "orderflow:shipcancel") }).ToArray();
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: ct);
        await _sender.SendInlineKeyboardAsync(callback.Message.Chat.Id.ToString(), "آدرس ارسال را انتخاب کنید:", buttons, ct);
    }

    private async Task<bool> TryHandleOrderFlowMessageAsync(TelegramMessage message, CancellationToken ct)
    {
        if (message.From is null || !_orderFlowDrafts.TryGet(message.Chat.Id, message.From.Id, out var draft))
            return false;
        if (!await IsAuthorizedInvoiceAdminAsync(message.Chat.Id, message.From.Id, ct))
        {
            _orderFlowDrafts.Remove(message.Chat.Id, message.From.Id);
            return true;
        }
        var input = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            await ReplyAsync(message.Chat.Id, "مقدار را به‌صورت متن ارسال کنید.", ct);
            return true;
        }
        if (input.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _orderFlowDrafts.Remove(message.Chat.Id, message.From.Id);
            await ReplyAsync(message.Chat.Id, "ثبت ارسال لغو شد.", ct);
            return true;
        }
        switch (draft.Stage)
        {
            case TelegramOrderShippingStage.AwaitingCompany:
                if (input.Length > 100) { await ReplyAsync(message.Chat.Id, "نام روش ارسال حداکثر ۱۰۰ کاراکتر است.", ct); return true; }
                draft.ShippingCompany = input;
                draft.Stage = TelegramOrderShippingStage.AwaitingCost;
                _orderFlowDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "هزینه ارسال را به تومان وارد کنید؛ برای ارسال رایگان عدد ۰ را بفرستید.", ct);
                return true;
            case TelegramOrderShippingStage.AwaitingCost:
                if (!TryParseOrderShippingCost(input, out var cost))
                {
                    await ReplyAsync(message.Chat.Id, "هزینه معتبر نیست؛ فقط عدد صفر یا بزرگ‌تر وارد کنید.", ct);
                    return true;
                }
                draft.ShippingCost = cost;
                draft.Stage = TelegramOrderShippingStage.AwaitingTrackingCode;
                _orderFlowDrafts.Set(draft);
                await ReplyAsync(message.Chat.Id, "کد رهگیری مرسوله را وارد کنید.", ct);
                return true;
            case TelegramOrderShippingStage.AwaitingTrackingCode:
                if (input.Length > 100) { await ReplyAsync(message.Chat.Id, "کد رهگیری حداکثر ۱۰۰ کاراکتر است.", ct); return true; }
                draft.TrackingCode = input;
                draft.Stage = TelegramOrderShippingStage.AwaitingConfirmation;
                _orderFlowDrafts.Set(draft);
                await SendShippingPreviewAsync(message.Chat.Id, draft, ct);
                return true;
            default:
                await ReplyAsync(message.Chat.Id, "از دکمه تأیید یا لغو زیر پیش‌نمایش استفاده کنید.", ct);
                return true;
        }
    }

    private async Task SendShippingPreviewAsync(long chatId, TelegramOrderShippingDraft draft, CancellationToken ct)
    {
        var order = await _db.Orders.AsNoTracking().Include(value => value.Customer)
            .FirstAsync(value => value.Id == draft.OrderId, ct);
        var address = await _db.Addresses.AsNoTracking().FirstAsync(value => value.Id == draft.AddressId, ct);
        var text = $"پیش‌نمایش ثبت ارسال\n\nسفارش: {order.OrderNumber}\nمشتری: {OrderCustomerLabel(order.Customer)}\n" +
                   $"گیرنده: {address.ReceiverName}\nشهر: {address.Province}، {address.City}\n" +
                   $"روش ارسال: {draft.ShippingCompany}\nهزینه: {draft.ShippingCost:N0} تومان\nکد رهگیری: {draft.TrackingCode}";
        await _sender.SendInlineKeyboardAsync(chatId.ToString(), text,
            new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[] { new TelegramInlineButton("✅ تأیید و ثبت ارسال", "orderflow:shipconfirm") },
                new[] { new TelegramInlineButton("❌ لغو", "orderflow:shipcancel") }
            }, ct);
    }

    private async Task ConfirmOrderShippingAsync(TelegramCallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat.Id;
        if (!_orderFlowDrafts.TryGet(chatId, callback.From.Id, out var draft) ||
            draft.Stage != TelegramOrderShippingStage.AwaitingConfirmation || !draft.AddressId.HasValue)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "فرایند ثبت ارسال منقضی شده است.", ct, true);
            return;
        }
        try
        {
            var result = await _mediator.Send(new CreateShipmentCommand(
                draft.OrderId, draft.AddressId.Value, draft.ShippingCompany,
                draft.ShippingCost, draft.TrackingCode, null), ct);
            _orderFlowDrafts.Remove(chatId, callback.From.Id);
            await _sender.AnswerCallbackAsync(callback.Id, "مرسوله ثبت شد ✅", ct, true);
            await ReplyAsync(chatId, $"سفارش {result.OrderStatus} شد و کد رهگیری {result.TrackingCode} ثبت شد ✅", ct);
            await SendOrderFlowDetailAsync(chatId, draft.OrderId, ct);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateConcurrencyException)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, ct, true);
        }
    }

    private static bool TryParseOrderShippingCost(string input, out decimal value)
    {
        var normalized = input.Replace(",", "").Replace("٬", "").Trim();
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static string OrderStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.Registered => "ثبت‌شده / در انتظار تکمیل لیست",
        OrderStatus.ListCompleted => "لیست تکمیل‌شده",
        OrderStatus.PerfumePurchased => "عطر خریداری‌شده",
        OrderStatus.Invoiced => "فاکتور‌شده / در انتظار پرداخت",
        OrderStatus.Paid => "پرداخت‌شده / منتظر دکانت",
        OrderStatus.Decanted => "دکانت‌شده",
        OrderStatus.ReadyToShip => "آماده ارسال",
        OrderStatus.Shipped => "ارسال‌شده",
        OrderStatus.Cancelled => "لغوشده",
        OrderStatus.Delivered => "تحویل‌شده",
        _ => status.ToString()
    };

    private static string OrderItemFulfillmentStatusLabel(OrderItemFulfillmentStatus status) => status switch
    {
        OrderItemFulfillmentStatus.WaitingForListCompletion => "انتظار تکمیل لیست",
        OrderItemFulfillmentStatus.ListCompleted => "لیست تکمیل‌شده",
        OrderItemFulfillmentStatus.AwaitingPurchase => "در انتظار خرید",
        OrderItemFulfillmentStatus.Purchased => "خرید شده",
        OrderItemFulfillmentStatus.Invoiced => "فاکتور شده",
        OrderItemFulfillmentStatus.WaitingForArrivalInIran => "منتظر رسیدن به ایران",
        OrderItemFulfillmentStatus.ArrivedInIran => "رسیده به ایران",
        OrderItemFulfillmentStatus.DecantQueue => "صف دکانت",
        OrderItemFulfillmentStatus.DecantedReadyToShip => "دکانت‌شده و آماده ارسال",
        OrderItemFulfillmentStatus.Shipped => "ارسال‌شده",
        _ => status.ToString()
    };

    private static string OrderCustomerLabel(Customer? customer)
    {
        if (!string.IsNullOrWhiteSpace(customer?.Username)) return "@" + customer.Username.TrimStart('@');
        return customer?.FullName ?? "مشتری نامشخص";
    }

    private static string OrderNumberSuffix(string orderNumber) =>
        orderNumber.Length <= 4 ? orderNumber : orderNumber[^4..];
}
