using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize(Roles = "TelegramBot")]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationOutboxRepository _repository;

    public NotificationsController(INotificationOutboxRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var notifications = await _repository.GetPendingAsync(limit, cancellationToken);
        return Ok(notifications.Select(value => new NotificationResponse(
            value.Id,
            value.EventType,
            value.Recipient,
            value.Payload,
            value.Attempts,
            value.CreatedAt)));
    }

    [HttpPost("{notificationId:guid}/processed")]
    public async Task<IActionResult> MarkProcessed(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);
        if (notification is null)
            return NotFound();

        if (notification.Status == NotificationOutboxStatus.Processed)
            return NoContent();

        notification.Status = NotificationOutboxStatus.Processed;
        notification.Attempts++;
        notification.ProcessedAt = DateTime.UtcNow;
        notification.LastError = null;
        notification.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{notificationId:guid}/failed")]
    public async Task<IActionResult> MarkFailed(
        Guid notificationId,
        MarkNotificationFailedRequest request,
        CancellationToken cancellationToken)
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);
        if (notification is null)
            return NotFound();

        notification.Attempts++;
        notification.Status = notification.Attempts >= 5
            ? NotificationOutboxStatus.Failed
            : NotificationOutboxStatus.Pending;
        notification.LastError = string.IsNullOrWhiteSpace(request.Error)
            ? "Telegram delivery failed."
            : request.Error.Trim()[..Math.Min(request.Error.Trim().Length, 1000)];
        notification.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record MarkNotificationFailedRequest(string? Error);

    public sealed record NotificationResponse(
        Guid Id,
        string EventType,
        string Recipient,
        string Payload,
        int Attempts,
        DateTime CreatedAt);
}
