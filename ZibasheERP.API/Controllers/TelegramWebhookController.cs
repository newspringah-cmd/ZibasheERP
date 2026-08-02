using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Orders.GetCustomerOrders;
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

        var message = update.Message;
        if (message?.From is null || string.IsNullOrWhiteSpace(message.Text))
            return Ok();

        if (!string.Equals(message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(
                message.Chat.Id,
                "برای حفظ حریم خصوصی، لطفاً این فرمان را در گفت‌وگوی خصوصی با ربات ارسال کنید.",
                cancellationToken);
            return Ok();
        }

        var response = TelegramCommandParser.Parse(message.Text) switch
        {
            TelegramCommand.Start => BuildWelcomeMessage(),
            TelegramCommand.Orders => await BuildOrdersMessageAsync(
                message.From.Id.ToString(),
                cancellationToken),
            _ => "فرمان را متوجه نشدم. برای مشاهده سفارش‌ها /orders را ارسال کنید."
        };

        await ReplyAsync(message.Chat.Id, response, cancellationToken);
        return Ok();
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

    private static string BuildWelcomeMessage() =>
        "به زیباشه خوش آمدید 🌿\nبرای مشاهده سفارش‌های خود، فرمان /orders را ارسال کنید.";

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
