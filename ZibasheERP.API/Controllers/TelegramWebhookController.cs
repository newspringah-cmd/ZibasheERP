using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Addresses.GetCustomerAddresses;
using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Features.Bottles.GetAvailableBottles;
using ZibasheERP.Application.Features.Invoices.GetOrderInvoice;
using ZibasheERP.Application.Features.Orders.CreateOrder;
using ZibasheERP.Application.Features.Orders.GetOrder;
using ZibasheERP.Application.Features.Orders.GetCustomerOrders;
using ZibasheERP.Application.Features.Orders.SetDeliveryAddress;
using ZibasheERP.Application.Features.Payments.GetPaymentBalance;
using ZibasheERP.Application.Features.Payments.SubmitPayment;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Features.SalesLists.GetOpenSalesLists;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Application.Notifications;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/telegram/webhook")]
[AllowAnonymous]
public sealed class TelegramWebhookController : ControllerBase
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private readonly IMediator _mediator;
    private readonly ITelegramMessageSender _sender;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramWebhookController> _logger;
    private readonly ITelegramOrderDraftRepository _draftRepository;

    public TelegramWebhookController(
        IMediator mediator,
        ITelegramMessageSender sender,
        IOptions<TelegramOptions> options,
        ITelegramOrderDraftRepository draftRepository,
        ILogger<TelegramWebhookController> logger)
    {
        _mediator = mediator;
        _sender = sender;
        _options = options.Value;
        _draftRepository = draftRepository;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(
        TelegramUpdate update,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.WebhookSecret))
            return NotFound();

        if (!Request.Headers.TryGetValue(SecretHeader, out var suppliedSecret) ||
            !SecretsMatch(suppliedSecret.ToString(), _options.WebhookSecret))
        {
            return Unauthorized();
        }

        if (update.CallbackQuery is not null)
        {
            await HandleCallbackAsync(update.CallbackQuery, cancellationToken);
            return Ok();
        }

        var message = update.Message;
        if (message?.From is null)
            return Ok();

        if (!string.Equals(message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(
                message.Chat.Id,
                "برای حفظ حریم خصوصی، لطفاً این فرمان را در گفت‌وگوی خصوصی با ربات ارسال کنید.",
                cancellationToken);
            return Ok();
        }

        if (message.Contact is not null)
        {
            var contactResponse = message.Contact.UserId != message.From.Id
                ? "برای امنیت حساب، فقط شماره متعلق به خودتان را با دکمه ربات ارسال کنید."
                : FormatLinkResult(await _mediator.Send(
                    new LinkTelegramCustomerCommand(
                        message.From.Id.ToString(),
                        message.Contact.PhoneNumber,
                        message.From.Username),
                    cancellationToken));

            await ReplyAsync(message.Chat.Id, contactResponse, cancellationToken);
            return Ok();
        }

        if (string.IsNullOrWhiteSpace(message.Text))
            return Ok();

        var paymentCommand = TelegramPaymentCommandParser.Parse(message.Text);
        if (paymentCommand is not null)
        {
            await SubmitTelegramPaymentAsync(
                message.Chat.Id,
                message.From.Id.ToString(),
                paymentCommand,
                cancellationToken);
            return Ok();
        }

        var command = TelegramCommandParser.Parse(message.Text);
        if (command == TelegramCommand.Lists)
        {
            await SendOpenListsAsync(message.Chat.Id, cancellationToken);
            return Ok();
        }

        if (command is TelegramCommand.Start or TelegramCommand.Orders or TelegramCommand.Addresses)
        {
            var usernameLink = await _mediator.Send(
                new LinkTelegramByUsernameCommand(
                    message.From.Id.ToString(),
                    message.From.Username),
                cancellationToken);

            if (!IsLinked(usernameLink))
            {
                if (usernameLink.Status == LinkTelegramCustomerStatus.UsernameNotFound)
                    await RequestContactAsync(message.Chat.Id, cancellationToken);
                else
                    await ReplyAsync(message.Chat.Id, FormatLinkResult(usernameLink), cancellationToken);
                return Ok();
            }

            if (command == TelegramCommand.Start)
            {
                await ReplyAsync(
                    message.Chat.Id,
                    $"{usernameLink.CustomerName} عزیز، به زیباشه خوش آمدید 🌿\nبرای مشاهده لیست‌ها /lists و سفارش‌های خود /orders را ارسال کنید.",
                    cancellationToken);
                return Ok();
            }

            if (command == TelegramCommand.Orders)
            {
                await SendOrdersAsync(
                    message.Chat.Id,
                    message.From.Id.ToString(),
                    cancellationToken);
                return Ok();
            }

            if (command == TelegramCommand.Addresses)
            {
                await SendAddressesAsync(
                    message.Chat.Id,
                    message.From.Id.ToString(),
                    cancellationToken);
                return Ok();
            }
        }

        var response = command switch
        {
            _ => "فرمان را متوجه نشدم.\n/lists لیست‌های فروش فعال\n/orders سفارش‌های من\n/addresses آدرس‌های من"
        };

        await ReplyAsync(message.Chat.Id, response, cancellationToken);
        return Ok();
    }

    private async Task SendOpenListsAsync(long chatId, CancellationToken cancellationToken)
    {
        var lists = await _mediator.Send(
            new GetOpenSalesListsQuery(10),
            cancellationToken);
        if (lists.Count == 0)
        {
            await ReplyAsync(chatId, "در حال حاضر لیست فروش فعالی وجود ندارد.", cancellationToken);
            return;
        }

        var lines = lists.Select((item, index) =>
        {
            var bottle = item.BottleOwnerAvailable ? "باتل آزاد" : "باتل رزرو";
            var name = string.IsNullOrWhiteSpace(item.PerfumeName)
                ? item.EnglishName
                : item.PerfumeName;
            return $"{index + 1}. {name} — {item.Brand}\n" +
                $"هر میل: {item.PricePerMl:N0} تومان | باقی‌مانده: {item.RemainingVolumeMl} میل | {bottle}";
        });

        var buttons = lists.Select(item =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"انتخاب {item.PerfumeName}",
                    $"list:{item.Id:N}")
            }).ToArray();
        var result = await _sender.SendInlineKeyboardAsync(
            chatId.ToString(),
            "لیست‌های فروش فعال:\n\n" + string.Join("\n\n", lines) +
                "\n\nبرای انتخاب، دکمه عطر موردنظر را بزنید.",
            buttons,
            cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram sales-list keyboard failed: {Error}", result.Error);
    }

    private async Task HandleCallbackAsync(
        TelegramCallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (callback.Message is null ||
            !string.Equals(callback.Message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase))
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "این عملیات فقط در گفت‌وگوی خصوصی ممکن است.",
                cancellationToken);
            return;
        }

        var selection = TelegramCallbackParser.Parse(callback.Data);
        if (selection.Type == TelegramCallbackType.Cancel)
        {
            await ReplyAsync(callback.Message.Chat.Id, "ثبت سفارش لغو شد.", cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.ConfirmOrder)
        {
            await ConfirmDraftAsync(callback, selection.SalesListId, cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.ViewOrder)
        {
            await SendOrderDetailsAsync(callback, selection.SalesListId, cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.StartPayment)
        {
            await SendPaymentInstructionsAsync(callback, selection.SalesListId, cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.ChooseDeliveryAddress)
        {
            await SendDeliveryAddressesAsync(callback, selection.SalesListId, cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.SetDeliveryAddress &&
            selection.BottleId is { } addressId)
        {
            await SetDeliveryAddressAsync(
                callback,
                selection.SalesListId,
                addressId,
                cancellationToken);
            return;
        }

        var lists = await _mediator.Send(new GetOpenSalesListsQuery(50), cancellationToken);
        var salesList = lists.FirstOrDefault(item => item.Id == selection.SalesListId);
        if (salesList is null)
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "این لیست دیگر فعال نیست.",
                cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.SelectSalesList)
        {
            var volumes = new[] { 5, 10, 15, 20, 30, 50 }
                .Where(value => value <= salesList.RemainingVolumeMl)
                .ToArray();
            if (volumes.Length == 0)
            {
                await _sender.AnswerCallbackAsync(
                    callback.Id,
                    "حجم قابل سفارشی باقی نمانده است.",
                    cancellationToken);
                return;
            }

            var rows = volumes
                .Chunk(3)
                .Select(row => (IReadOnlyCollection<TelegramInlineButton>)row
                    .Select(volume => new TelegramInlineButton(
                        $"{volume} میل",
                        $"volume:{salesList.Id:N}:{volume}"))
                    .ToArray())
                .ToArray();
            await _sender.SendInlineKeyboardAsync(
                callback.Message.Chat.Id.ToString(),
                $"{salesList.PerfumeName} انتخاب شد. حجم موردنظر را انتخاب کنید:",
                rows,
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.SelectVolume &&
            selection.VolumeMl is { } volume &&
            volume <= salesList.RemainingVolumeMl)
        {
            var bottles = await _mediator.Send(
                new GetAvailableBottlesQuery(volume),
                cancellationToken);
            if (bottles.Count == 0)
            {
                await _sender.AnswerCallbackAsync(
                    callback.Id,
                    "برای این حجم شیشه فعالی وجود ندارد.",
                    cancellationToken);
                return;
            }

            var listToken = TelegramCallbackParser.EncodeGuid(salesList.Id);
            var rows = bottles.Select(bottle =>
                (IReadOnlyCollection<TelegramInlineButton>)new[]
                {
                    new TelegramInlineButton(
                        $"{bottle.Name} — {bottle.Price:N0} تومان",
                        $"b:{listToken}:{volume}:{TelegramCallbackParser.EncodeGuid(bottle.Id)}")
                }).ToArray();
            await _sender.SendInlineKeyboardAsync(
                callback.Message.Chat.Id.ToString(),
                $"{salesList.PerfumeName}، حجم {volume} میل انتخاب شد. شیشه را انتخاب کنید:",
                rows,
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        if (selection.Type == TelegramCallbackType.SelectBottle &&
            selection.VolumeMl is { } selectedVolume &&
            selection.BottleId is { } bottleId &&
            selectedVolume <= salesList.RemainingVolumeMl)
        {
            var bottles = await _mediator.Send(
                new GetAvailableBottlesQuery(selectedVolume),
                cancellationToken);
            var bottle = bottles.FirstOrDefault(item => item.Id == bottleId);
            if (bottle is null)
            {
                await _sender.AnswerCallbackAsync(
                    callback.Id,
                    "این شیشه دیگر قابل انتخاب نیست.",
                    cancellationToken);
                return;
            }

            var draft = new TelegramOrderDraft
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                TelegramId = callback.From.Id.ToString(),
                SalesListId = salesList.Id,
                VolumeMl = selectedVolume,
                BottleId = bottle.Id,
                ExpiresAt = DateTime.UtcNow.AddMinutes(15)
            };
            await _draftRepository.AddAsync(draft, cancellationToken);
            await _draftRepository.SaveChangesAsync(cancellationToken);

            var total = salesList.PricePerMl * selectedVolume + bottle.Price;
            var rows = new IReadOnlyCollection<TelegramInlineButton>[]
            {
                new[]
                {
                    new TelegramInlineButton(
                        "تأیید و ثبت سفارش",
                        $"confirm:{TelegramCallbackParser.EncodeGuid(draft.Id)}")
                },
                new[] { new TelegramInlineButton("انصراف", "cancel") }
            };
            await _sender.SendInlineKeyboardAsync(
                callback.Message.Chat.Id.ToString(),
                $"خلاصه سفارش:\n{salesList.PerfumeName} — {selectedVolume} میل\n" +
                $"شیشه: {bottle.Name}\nمبلغ نهایی: {total:N0} تومان\n\nآیا سفارش ثبت شود؟",
                rows,
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        await _sender.AnswerCallbackAsync(
            callback.Id,
            "انتخاب نامعتبر است.",
            cancellationToken);
    }

    private async Task ConfirmDraftAsync(
        TelegramCallbackQuery callback,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draft = await _draftRepository.GetByIdAsync(draftId, cancellationToken);
        if (draft is null || draft.TelegramId != callback.From.Id.ToString())
        {
            await _sender.AnswerCallbackAsync(callback.Id, "درخواست سفارش معتبر نیست.", cancellationToken);
            return;
        }

        if (draft.Status == TelegramOrderDraftStatus.Completed && draft.OrderId.HasValue)
        {
            await SendOrderRegisteredAsync(
                callback.Message!.Chat.Id,
                draft.OrderId.Value,
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, "این سفارش قبلاً ثبت شده است.", cancellationToken);
            return;
        }

        if (draft.ExpiresAt <= DateTime.UtcNow)
        {
            draft.Status = TelegramOrderDraftStatus.Expired;
            draft.UpdatedAt = DateTime.UtcNow;
            await _draftRepository.SaveChangesAsync(cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, "زمان تأیید سفارش به پایان رسیده است.", cancellationToken);
            return;
        }

        var link = await _mediator.Send(
            new LinkTelegramByUsernameCommand(
                callback.From.Id.ToString(),
                callback.From.Username),
            cancellationToken);
        if (!IsLinked(link))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "ابتدا حساب خود را با /start متصل کنید.", cancellationToken);
            return;
        }

        try
        {
            var orderId = await _mediator.Send(
                new CreateOrderCommand
                {
                    TelegramId = callback.From.Id.ToString(),
                    SalesListId = draft.SalesListId,
                    RequestedVolumeMl = draft.VolumeMl,
                    IsBottleOwner = false,
                    BottleId = draft.BottleId,
                    ExternalReference = $"telegram-draft:{draft.Id:N}"
                },
                cancellationToken);
            draft.Status = TelegramOrderDraftStatus.Completed;
            draft.OrderId = orderId;
            draft.UpdatedAt = DateTime.UtcNow;
            await _draftRepository.SaveChangesAsync(cancellationToken);
            await SendOrderRegisteredAsync(callback.Message!.Chat.Id, orderId, cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, "سفارش ثبت شد.", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, cancellationToken);
        }
    }

    private async Task SendOrderRegisteredAsync(
        long chatId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(new GetOrderQuery(orderId), cancellationToken);
        var message = order is null
            ? "سفارش با موفقیت ثبت شد."
            : $"سفارش {order.OrderNumber} با مبلغ {order.FinalAmount:N0} تومان ثبت شد.";
        await ReplyAsync(chatId, message, cancellationToken);
    }

    private async Task RequestContactAsync(long chatId, CancellationToken cancellationToken)
    {
        var result = await _sender.RequestContactAsync(
            chatId.ToString(),
            "به زیباشه خوش آمدید 🌿\nبرای اتصال امن حساب و مشاهده سفارش‌ها، شماره موبایل خود را با دکمه زیر ارسال کنید.",
            cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram contact request failed: {Error}", result.Error);
    }

    private async Task SendOrdersAsync(
        long chatId,
        string telegramId,
        CancellationToken cancellationToken)
    {
        var orders = await _mediator.Send(
            new GetCustomerOrdersQuery(null, telegramId),
            cancellationToken);

        if (orders.Count == 0)
        {
            await ReplyAsync(
                chatId,
                "سفارشی برای حساب تلگرام شما پیدا نشد. اگر قبلاً سفارش داشته‌اید، با پشتیبانی تماس بگیرید.",
                cancellationToken);
            return;
        }

        var recentOrders = orders
            .OrderByDescending(order => order.RegisteredAt)
            .Take(10)
            .ToArray();
        var lines = recentOrders.Select(order =>
            $"• {order.OrderNumber} — {TranslateStatus(order.Status)} — {order.FinalAmount:N0} تومان");
        var rows = recentOrders.Select(order =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"جزئیات {order.OrderNumber}",
                    $"order:{TelegramCallbackParser.EncodeGuid(order.Id)}")
            }).ToArray();
        var result = await _sender.SendInlineKeyboardAsync(
            chatId.ToString(),
            "آخرین سفارش‌های شما:\n\n" + string.Join("\n", lines),
            rows,
            cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram orders keyboard failed: {Error}", result.Error);
    }

    private async Task SendAddressesAsync(
        long chatId,
        string telegramId,
        CancellationToken cancellationToken)
    {
        var addresses = await _mediator.Send(
            new GetCustomerAddressesQuery(null, telegramId),
            cancellationToken);
        if (addresses.Count == 0)
        {
            await ReplyAsync(
                chatId,
                "هنوز آدرسی برای حساب شما ثبت نشده است. برای ثبت آدرس با پشتیبانی تماس بگیرید.",
                cancellationToken);
            return;
        }

        var lines = addresses.Select((address, index) =>
            $"{index + 1}. {(address.IsDefault ? "⭐ " : string.Empty)}{address.Description ?? "آدرس"}\n" +
            $"{address.Province}، {address.City}، {address.FullAddress}\n" +
            $"گیرنده: {address.ReceiverName} — {address.Mobile}\nکدپستی: {address.PostalCode}");
        await ReplyAsync(
            chatId,
            "آدرس‌های ثبت‌شده شما:\n\n" + string.Join("\n\n", lines),
            cancellationToken);
    }

    private async Task SendOrderDetailsAsync(
        TelegramCallbackQuery callback,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(new GetOrderQuery(orderId), cancellationToken);
        if (order is null || order.Customer.TelegramId != callback.From.Id.ToString())
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "سفارش پیدا نشد یا متعلق به این حساب نیست.",
                cancellationToken);
            return;
        }

        var invoice = await _mediator.Send(
            new GetOrderInvoiceQuery(order.Id),
            cancellationToken);
        var items = order.Items.Select(item =>
            $"• {item.PerfumeName} — {item.RequestedVolumeMl} میل — {item.LineTotal:N0} تومان");
        var invoiceLine = invoice is null
            ? "فاکتور: هنوز صادر نشده"
            : $"فاکتور: {invoice.InvoiceNumber} — {invoice.Status}";
        var details = $"سفارش {order.OrderNumber}\nوضعیت: {TranslateStatus(order.Status)}\n" +
            $"{string.Join("\n", items)}\n\n{invoiceLine}\nمبلغ نهایی: {order.FinalAmount:N0} تومان";
        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>();
        if (invoice is not null && order.Status != "Paid" && order.Status != "Cancelled")
        {
            rows.Add(new[]
            {
                new TelegramInlineButton(
                    "ثبت پرداخت",
                    $"pay:{TelegramCallbackParser.EncodeGuid(order.Id)}")
            });
        }
        if (order.Status is not ("Shipped" or "Delivered" or "Cancelled"))
        {
            rows.Add(new[]
            {
                new TelegramInlineButton(
                    order.DeliveryAddressId.HasValue ? "تغییر آدرس تحویل" : "انتخاب آدرس تحویل",
                    $"shipaddr:{TelegramCallbackParser.EncodeGuid(order.Id)}")
            });
        }

        if (rows.Count > 0)
        {
            await _sender.SendInlineKeyboardAsync(
                callback.Message!.Chat.Id.ToString(),
                details,
                rows,
                cancellationToken);
        }
        else
        {
            await ReplyAsync(callback.Message!.Chat.Id, details, cancellationToken);
        }
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
    }

    private async Task SendDeliveryAddressesAsync(
        TelegramCallbackQuery callback,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(new GetOrderQuery(orderId), cancellationToken);
        if (order is null ||
            order.Customer.TelegramId != callback.From.Id.ToString() ||
            order.Status is "Shipped" or "Delivered" or "Cancelled")
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "امکان انتخاب آدرس برای این سفارش وجود ندارد.",
                cancellationToken);
            return;
        }

        var addresses = await _mediator.Send(
            new GetCustomerAddressesQuery(null, callback.From.Id.ToString()),
            cancellationToken);
        if (addresses.Count == 0)
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "ابتدا باید یک آدرس برای حساب شما ثبت شود.",
                cancellationToken);
            return;
        }

        var orderToken = TelegramCallbackParser.EncodeGuid(order.Id);
        var rows = addresses.Select(address =>
            (IReadOnlyCollection<TelegramInlineButton>)new[]
            {
                new TelegramInlineButton(
                    $"{(address.IsDefault ? "⭐ " : string.Empty)}{address.Description ?? address.City}",
                    $"setaddr:{orderToken}:{TelegramCallbackParser.EncodeGuid(address.Id)}")
            }).ToArray();
        await _sender.SendInlineKeyboardAsync(
            callback.Message!.Chat.Id.ToString(),
            "آدرس تحویل را انتخاب کنید:",
            rows,
            cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
    }

    private async Task SetDeliveryAddressAsync(
        TelegramCallbackQuery callback,
        Guid orderId,
        Guid addressId,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(new GetOrderQuery(orderId), cancellationToken);
        if (order is null || order.Customer.TelegramId != callback.From.Id.ToString())
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "سفارش متعلق به این حساب نیست.",
                cancellationToken);
            return;
        }

        try
        {
            var result = await _mediator.Send(
                new SetOrderDeliveryAddressCommand(orderId, addressId),
                cancellationToken);
            await ReplyAsync(
                callback.Message!.Chat.Id,
                $"آدرس تحویل سفارش ثبت شد:\n{result.City}، {result.FullAddress}",
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, "آدرس ثبت شد.", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await _sender.AnswerCallbackAsync(callback.Id, exception.Message, cancellationToken);
        }
    }

    private async Task SendPaymentInstructionsAsync(
        TelegramCallbackQuery callback,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(new GetOrderQuery(orderId), cancellationToken);
        var invoice = await _mediator.Send(new GetOrderInvoiceQuery(orderId), cancellationToken);
        if (order is null ||
            invoice is null ||
            order.Customer.TelegramId != callback.From.Id.ToString())
        {
            await _sender.AnswerCallbackAsync(
                callback.Id,
                "امکان ثبت پرداخت برای این سفارش وجود ندارد.",
                cancellationToken);
            return;
        }

        await ReplyAsync(
            callback.Message!.Chat.Id,
            $"پس از واریز مبلغ، شناسه تراکنش را با قالب زیر ارسال کنید:\n\n" +
            $"/pay {order.OrderNumber} شناسه_تراکنش\n\n" +
            $"مبلغ فاکتور: {invoice.TotalAmount:N0} تومان",
            cancellationToken);
        await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
    }

    private async Task SubmitTelegramPaymentAsync(
        long chatId,
        string telegramId,
        TelegramPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var balance = await _mediator.Send(
            new GetPaymentBalanceQuery(command.OrderNumber),
            cancellationToken);
        if (balance is null || balance.TelegramId != telegramId)
        {
            await ReplyAsync(chatId, "سفارش پیدا نشد یا متعلق به حساب شما نیست.", cancellationToken);
            return;
        }

        if (balance.OrderStatus is "Paid" or "Cancelled" || balance.RemainingAmount <= 0)
        {
            await ReplyAsync(chatId, "این سفارش مانده قابل پرداخت ندارد.", cancellationToken);
            return;
        }

        if (balance.OrderStatus != "Invoiced")
        {
            await ReplyAsync(chatId, "ابتدا باید فاکتور سفارش توسط ادمین صادر شود.", cancellationToken);
            return;
        }

        try
        {
            var result = await _mediator.Send(
                new SubmitPaymentCommand(
                    balance.OrderId,
                    balance.RemainingAmount,
                    "Telegram",
                    command.TransactionId,
                    "ثبت‌شده توسط مشتری در ربات تلگرام"),
                cancellationToken);
            await ReplyAsync(
                chatId,
                $"پرداخت {result.Amount:N0} تومانی ثبت شد و در انتظار تأیید ادمین است.",
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await ReplyAsync(chatId, exception.Message, cancellationToken);
        }
    }

    private async Task ReplyAsync(
        long chatId,
        string response,
        CancellationToken cancellationToken)
    {
        var result = await _sender.SendAsync(chatId.ToString(), response, cancellationToken);
        if (!result.IsSuccessful)
            _logger.LogWarning("Telegram webhook reply failed: {Error}", result.Error);
    }

    private static string FormatLinkResult(LinkTelegramCustomerResult result) => result.Status switch
    {
        LinkTelegramCustomerStatus.Linked =>
            $"{result.CustomerName} عزیز، حساب شما با موفقیت متصل شد. برای مشاهده سفارش‌ها /orders را ارسال کنید.",
        LinkTelegramCustomerStatus.AlreadyLinked =>
            $"{result.CustomerName} عزیز، حساب شما قبلاً متصل شده است. برای مشاهده سفارش‌ها /orders را ارسال کنید.",
        LinkTelegramCustomerStatus.InvalidMobile =>
            "شماره موبایل معتبر نیست. لطفاً از دکمه «ارسال شماره موبایل» استفاده کنید.",
        LinkTelegramCustomerStatus.CustomerNotFound =>
            "این شماره در زیباشه ثبت نشده است. لطفاً با پشتیبانی تماس بگیرید.",
        LinkTelegramCustomerStatus.TelegramAlreadyLinked =>
            "این حساب تلگرام قبلاً به شماره دیگری متصل شده است. لطفاً با پشتیبانی تماس بگیرید.",
        LinkTelegramCustomerStatus.CustomerLinkedToAnotherTelegram =>
            "این شماره قبلاً به حساب تلگرام دیگری متصل شده است. لطفاً با پشتیبانی تماس بگیرید.",
        LinkTelegramCustomerStatus.UsernameNotFound =>
            "Username شما در اطلاعات مشتریان پیدا نشد. لطفاً شماره موبایل خود را با دکمه ربات ارسال کنید.",
        LinkTelegramCustomerStatus.UsernameLinkedToAnotherTelegram =>
            "این Username قبلاً به حساب تلگرام دیگری متصل شده است. لطفاً با پشتیبانی تماس بگیرید.",
        _ => "اتصال حساب انجام نشد. لطفاً دوباره تلاش کنید."
    };

    private static bool IsLinked(LinkTelegramCustomerResult result) =>
        result.Status is LinkTelegramCustomerStatus.Linked or
            LinkTelegramCustomerStatus.AlreadyLinked;

    private static string TranslateStatus(string status) => status switch
    {
        "Registered" => "ثبت‌شده",
        "ListCompleted" => "تکمیل لیست",
        "PerfumePurchased" => "خرید عطر",
        "Invoiced" => "فاکتور صادرشده",
        "Paid" => "پرداخت‌شده",
        "Decanted" => "دکانت‌شده",
        "ReadyToShip" => "آماده ارسال",
        "Shipped" => "ارسال‌شده",
        "Delivered" => "تحویل‌شده",
        "Cancelled" => "لغوشده",
        _ => status
    };

    private static bool SecretsMatch(string supplied, string configured)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(configured))
            return false;

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        return suppliedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
