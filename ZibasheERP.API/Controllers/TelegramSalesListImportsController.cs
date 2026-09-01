using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/telegram-sales-list-imports")]
[Authorize(Roles = "Admin")]
public sealed class TelegramSalesListImportsController(
    AppDbContext db,
    ITelegramMessageSender sender,
    IOptions<TelegramOptions> options) : ControllerBase
{
    [HttpPost("pilot")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> CreatePilot(
        [FromForm] string sourceChannelId,
        [FromForm] long sourceMessageId,
        [FromForm] DateTimeOffset sourceDate,
        [FromForm] string sourcePhotoPath,
        [FromForm] string rawText,
        [FromForm] string parsedPayload,
        [FromForm] string parseIssues,
        [FromForm] IFormFile photo,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceChannelId) || sourceMessageId <= 0 ||
            string.IsNullOrWhiteSpace(rawText) || photo.Length == 0 || photo.Length > 7 * 1024 * 1024)
            return BadRequest(new { Message = "اطلاعات لیست یا عکس معتبر نیست." });

        var exists = await db.TelegramSalesListImports.AnyAsync(value =>
            value.SourceChannelId == sourceChannelId && value.SourceMessageId == sourceMessageId,
            cancellationToken);
        if (exists) return Conflict(new { Message = "این پیام قبلاً وارد صف شده است." });

        await using var stream = photo.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var item = new TelegramSalesListImport
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
            SourceChannelId = sourceChannelId.Trim(), SourceMessageId = sourceMessageId,
            SourceDate = sourceDate, SourcePhotoPath = sourcePhotoPath?.Trim() ?? string.Empty,
            RawText = rawText, ParsedPayload = parsedPayload, ParseIssues = parseIssues,
            Status = TelegramSalesListImportStatus.PendingReview,
            ReviewChatId = options.Value.SalesAuditChatId
        };
        db.TelegramSalesListImports.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        var reviewText = BuildReviewText(item, parsedPayload);
        var buttons = new[]
        {
            new[] { new TelegramInlineButton("✅ تأیید", $"import:approve:{item.Id:N}"),
                    new TelegramInlineButton("✏️ ویرایش", $"import:edit:{item.Id:N}"),
                    new TelegramInlineButton("❌ رد", $"import:reject:{item.Id:N}") }
        };
        var sent = await sender.SendPhotoBytesWithKeyboardAsync(
            options.Value.SalesAuditChatId, memory.ToArray(), photo.FileName,
            reviewText, Array.Empty<IReadOnlyCollection<TelegramInlineButton>>(), cancellationToken);
        if (!sent.IsSuccessful)
        {
            item.Status = TelegramSalesListImportStatus.Failed;
            item.LastError = sent.Error;
            await db.SaveChangesAsync(cancellationToken);
            return Problem(sent.Error);
        }

        item.ReviewMessageId = sent.MessageId;
        item.TelegramPhotoFileId = sent.ExternalFileId;
        await db.SaveChangesAsync(cancellationToken);

        foreach (var chunk in SplitForTelegram(item.RawText, 3800))
            await sender.SendInlineKeyboardAsync(options.Value.SalesAuditChatId, chunk, buttons, cancellationToken);
        return Ok(new { item.Id, item.ReviewMessageId });
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
        var price = parsed.TryGetProperty("pricePerMl", out var priceValue) ? priceValue.ToString() : "-";
        return $"🔎 بررسی واردات لیست فروش\nکد: {code}\nعطر: {name}\nقیمت هر میل: {price}\nپیام مبدأ: {item.SourceMessageId}";
    }
}
