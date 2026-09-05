using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Features.Integrations.RecordOrderArtifact;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/integrations/n8n")]
[Authorize(Roles = "N8n")]
public sealed class N8nIntegrationsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly AppDbContext _context;
    private readonly TelegramOptions _telegramOptions;
    private readonly ITelegramMessageSender _telegramSender;

    public N8nIntegrationsController(
        IMediator mediator,
        AppDbContext context,
        IOptions<TelegramOptions> telegramOptions,
        ITelegramMessageSender telegramSender)
    {
        _mediator = mediator;
        _context = context;
        _telegramOptions = telegramOptions.Value;
        _telegramSender = telegramSender;
    }

    [HttpPost("telegram-invoices")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> SendTelegramInvoice(
        [FromForm] Guid sourceEventId,
        [FromForm] string chatId,
        [FromForm] string caption,
        [FromForm] IFormFile document,
        CancellationToken cancellationToken)
    {
        chatId = chatId.Trim();
        caption = caption.Trim();
        if (!long.TryParse(chatId, out var numericChatId) || numericChatId >= 0 ||
            document.Length == 0 || document.Length > 20 * 1024 * 1024 ||
            caption.Length > 1024)
        {
            return BadRequest(new { Message = "اطلاعات فایل یا مقصد فاکتور معتبر نیست." });
        }

        var sourceEvent = await _context.NotificationOutbox
            .AsNoTracking()
            .FirstOrDefaultAsync(value =>
                value.Id == sourceEventId &&
                value.Channel == "N8n" &&
                value.EventType == "InvoiceIssued",
                cancellationToken);
        if (sourceEvent is null)
            return NotFound(new { Message = "رویداد فاکتور پیدا نشد." });
        var isCustomerDelivery = N8nDeliveryTargetValidator.MatchesTelegramGroup(sourceEvent.Payload, chatId);
        var isManualReviewDelivery = !N8nDeliveryTargetValidator.HasApprovedTelegramGroup(sourceEvent.Payload) &&
            string.Equals(chatId, _telegramOptions.InvoiceFailureChatId.Trim(), StringComparison.Ordinal);
        if (!isCustomerDelivery && !isManualReviewDelivery)
            return Conflict(new { Message = "مقصد فاکتور با گروه تأییدشده یکسان نیست." });

        using var payload = JsonDocument.Parse(sourceEvent.Payload);
        var data = payload.RootElement;
        var invoiceId = data.TryGetProperty("InvoiceId", out var invoiceIdElement) &&
            invoiceIdElement.TryGetGuid(out var parsedInvoiceId)
                ? parsedInvoiceId
                : Guid.Empty;
        if (invoiceId == Guid.Empty)
            return BadRequest(new { Message = "شناسه فاکتور در رویداد معتبر نیست." });

        var rows = new List<IReadOnlyCollection<TelegramInlineButton>>
        {
            new TelegramInlineButton[]
            {
                new("✅ پرداخت‌شده", $"invoicepay:paid:{invoiceId:N}"),
                new("⏳ در انتظار پرداخت", $"invoicepay:waiting:{invoiceId:N}")
            }
        };
        if (data.TryGetProperty("PaymentAccounts", out var accounts) &&
            accounts.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accounts.EnumerateArray().Take(4))
            {
                var card = account.TryGetProperty("CardNumber", out var cardElement)
                    ? cardElement.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(card))
                    continue;
                var bank = account.TryGetProperty("BankName", out var bankElement)
                    ? bankElement.GetString()?.Trim()
                    : null;
                rows.Add(new TelegramInlineButton[]
                {
                    new($"📋 کپی شماره کارت {bank ?? string.Empty}".Trim(), CopyText: card)
                });
            }
        }
        if (isManualReviewDelivery &&
            data.TryGetProperty("InvoiceNumber", out var invoiceNumberElement) &&
            !string.IsNullOrWhiteSpace(invoiceNumberElement.GetString()))
        {
            rows.Add(new TelegramInlineButton[]
            {
                new("📋 کپی فرمان اتصال گروه",
                    CopyText: $"/connect {invoiceNumberElement.GetString()!.Trim()}")
            });
        }

        await using var stream = document.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var result = await _telegramSender.SendDocumentWithKeyboardAsync(
            chatId,
            buffer.ToArray(),
            string.IsNullOrWhiteSpace(document.FileName) ? "invoice.pdf" : document.FileName,
            caption,
            rows,
            cancellationToken);
        if (!result.IsSuccessful)
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = result.Error });

        return Ok(new
        {
            result = new
            {
                message_id = result.MessageId,
                document = new { file_id = result.ExternalFileId }
            }
        });
    }

    [HttpPost("order-artifacts")]
    public async Task<IActionResult> RecordOrderArtifact(
        RecordOrderArtifactRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OrderArtifactType>(request.Type, true, out var type) ||
            !Enum.IsDefined(type))
        {
            return BadRequest(new { Message = "نوع فایل سفارش معتبر نیست." });
        }

        return Ok(await _mediator.Send(
            new RecordOrderArtifactCommand(
                request.SourceEventId,
                request.OrderId,
                type,
                request.FileUrl,
                request.ExternalFileId,
                request.ContentType),
            cancellationToken));
    }

    public sealed record RecordOrderArtifactRequest(
        Guid SourceEventId,
        Guid OrderId,
        string Type,
        string? FileUrl,
        string? ExternalFileId,
        string? ContentType);

    [HttpPost("delivery-failures")]
    public async Task<IActionResult> ReportDeliveryFailure(
        ReportN8nDeliveryFailureRequest request,
        CancellationToken cancellationToken)
    {
        var chatId = request.ChatId.Trim();
        var error = request.Error.Trim();
        if (!long.TryParse(chatId, out var numericChatId) || numericChatId >= 0 ||
            string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(new { Message = "شناسه گروه یا متن خطا معتبر نیست." });
        }

        var existing = await _context.IntegrationDeliveryFailures
            .AsNoTracking()
            .FirstOrDefaultAsync(
                failure => failure.SourceEventId == request.SourceEventId,
                cancellationToken);
        if (existing is not null)
            return Ok(ToFailureResponse(existing, true));

        var sourceEvent = await _context.NotificationOutbox
            .AsNoTracking()
            .FirstOrDefaultAsync(
                value => value.Id == request.SourceEventId && value.Channel == "N8n",
                cancellationToken);
        if (sourceEvent is null)
            return NotFound(new { Message = "رویداد مبدأ n8n پیدا نشد." });
        if (!N8nDeliveryTargetValidator.MatchesTelegramGroup(sourceEvent.Payload, chatId))
            return Conflict(new { Message = "مقصد گزارش با مقصد مجاز رویداد یکسان نیست." });

        var group = await _context.CustomerTelegramGroups.FirstOrDefaultAsync(
            value => value.ChatId == chatId &&
                value.CustomerId == sourceEvent.CustomerId &&
                !value.IsDeleted,
            cancellationToken);
        if (group is null)
            return NotFound(new { Message = "نگاشت گروه مشتری پیدا نشد." });

        var now = DateTime.UtcNow;
        group.IsActive = false;
        group.UpdatedAt = now;
        var failure = new IntegrationDeliveryFailure
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            SourceEventId = sourceEvent.Id,
            CustomerId = sourceEvent.CustomerId,
            OrderId = sourceEvent.OrderId,
            CustomerTelegramGroupId = group.Id,
            Recipient = chatId,
            Error = error.Length <= 1000 ? error : error[..1000],
            ReportedAt = now
        };
        _context.IntegrationDeliveryFailures.Add(failure);

        if (sourceEvent.OrderId.HasValue)
        {
            var invoice = await _context.Invoices.FirstOrDefaultAsync(
                value => value.OrderId == sourceEvent.OrderId.Value && !value.IsDeleted,
                cancellationToken);
            if (invoice is not null)
            {
                invoice.DeliveryStatus = InvoiceDeliveryStatus.NeedsManualAction;
                invoice.DeliveryStatusChangedAt = now;
                invoice.DeliveryStatusNote = failure.Error;
                invoice.UpdatedAt = now;
            }
        }

        var adminChatId = string.IsNullOrWhiteSpace(_telegramOptions.InvoiceFailureChatId)
            ? _telegramOptions.AdminChatId.Trim()
            : _telegramOptions.InvoiceFailureChatId.Trim();
        if (!string.IsNullOrWhiteSpace(adminChatId))
        {
            var alert = new NotificationOutbox
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                CustomerId = sourceEvent.CustomerId,
                OrderId = sourceEvent.OrderId,
                Channel = "Telegram",
                EventType = "TelegramGroupDeliveryFailed",
                Recipient = adminChatId,
                Payload = JsonSerializer.Serialize(new
                {
                    sourceEvent.CustomerId,
                    GroupChatId = chatId,
                    NotificationId = sourceEvent.Id,
                    Source = "n8n",
                    Error = failure.Error
                })
            };
            failure.AdminNotificationId = alert.Id;
            _context.NotificationOutbox.Add(alert);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            var concurrent = await _context.IntegrationDeliveryFailures
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    value => value.SourceEventId == request.SourceEventId,
                    cancellationToken);
            if (concurrent is null)
                throw;
            return Ok(ToFailureResponse(concurrent, true));
        }
        return Ok(ToFailureResponse(failure, false));
    }

    private static object ToFailureResponse(
        IntegrationDeliveryFailure failure,
        bool duplicate) => new
        {
            failure.Id,
            failure.SourceEventId,
            failure.CustomerId,
            failure.OrderId,
            failure.Recipient,
            failure.ReportedAt,
            failure.AdminNotificationId,
            Duplicate = duplicate
        };

    public sealed record ReportN8nDeliveryFailureRequest(
        Guid SourceEventId,
        string ChatId,
        string Error);
}
