using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Customers.LinkTelegram;
using ZibasheERP.Application.Features.Orders.GetCustomerOrders;
using ZibasheERP.Application.Features.SalesLists.GetOpenSalesLists;
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

    public TelegramWebhookController(
        IMediator mediator,
        ITelegramMessageSender sender,
        IOptions<TelegramOptions> options,
        ILogger<TelegramWebhookController> logger)
    {
        _mediator = mediator;
        _sender = sender;
        _options = options.Value;
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

        var command = TelegramCommandParser.Parse(message.Text);
        if (command == TelegramCommand.Lists)
        {
            await SendOpenListsAsync(message.Chat.Id, cancellationToken);
            return Ok();
        }

        if (command is TelegramCommand.Start or TelegramCommand.Orders)
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
        }

        var response = command switch
        {
            TelegramCommand.Orders => await BuildOrdersMessageAsync(
                message.From.Id.ToString(),
                cancellationToken),
            _ => "فرمان را متوجه نشدم.\n/lists لیست‌های فروش فعال\n/orders سفارش‌های من"
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
            await ReplyAsync(
                callback.Message.Chat.Id,
                $"{salesList.PerfumeName}، حجم {volume} میل انتخاب شد.\nدر مرحله بعد انتخاب شیشه و تأیید نهایی سفارش اضافه می‌شود.",
                cancellationToken);
            await _sender.AnswerCallbackAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        await _sender.AnswerCallbackAsync(
            callback.Id,
            "انتخاب نامعتبر است.",
            cancellationToken);
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

    private async Task<string> BuildOrdersMessageAsync(
        string telegramId,
        CancellationToken cancellationToken)
    {
        var orders = await _mediator.Send(
            new GetCustomerOrdersQuery(null, telegramId),
            cancellationToken);

        if (orders.Count == 0)
            return "سفارشی برای حساب تلگرام شما پیدا نشد. اگر قبلاً سفارش داشته‌اید، با پشتیبانی تماس بگیرید.";

        var lines = orders
            .OrderByDescending(order => order.RegisteredAt)
            .Take(10)
            .Select(order =>
                $"• {order.OrderNumber} — {TranslateStatus(order.Status)} — {order.FinalAmount:N0} تومان");
        return "آخرین سفارش‌های شما:\n\n" + string.Join("\n", lines);
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
