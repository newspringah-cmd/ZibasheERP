using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using MediatR;
using ZibasheERP.Application.Features.Notifications.ManageFailedNotifications;
using ZibasheERP.Application.Notifications;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationOutboxRepository _repository;
    private readonly IMediator _mediator;

    public NotificationsController(INotificationOutboxRepository repository, IMediator mediator)
    {
        _repository = repository;
        _mediator = mediator;
    }

    [HttpGet("{notificationId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetStatus(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var notification = await _repository.GetByIdAsync(notificationId, cancellationToken);
        if (notification is null)
            return NotFound();

        return Ok(new NotificationStatusResponse(
            notification.Id,
            notification.Channel,
            notification.EventType,
            notification.Status.ToString(),
            notification.Attempts,
            notification.LastError,
            notification.CreatedAt,
            notification.ProcessedAt));
    }

    [HttpGet("pending")]
    [Authorize(Roles = "TelegramBot")]
    public async Task<IActionResult> GetPending(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var notifications = await _repository.GetPendingAsync("Telegram", limit, cancellationToken);
        return Ok(notifications.Select(value => new NotificationResponse(
            value.Id,
            value.EventType,
            value.Recipient,
            value.Payload,
            value.Attempts,
            value.CreatedAt)));
    }

    [HttpPost("{notificationId:guid}/processed")]
    [Authorize(Roles = "TelegramBot")]
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
        notification.LockedUntil = null;
        notification.NextAttemptAt = null;
        notification.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{notificationId:guid}/failed")]
    [Authorize(Roles = "TelegramBot")]
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
        notification.LockedUntil = null;
        notification.NextAttemptAt = notification.Status == NotificationOutboxStatus.Pending
            ? DateTime.UtcNow + NotificationRetryPolicy.DelayAfter(notification.Attempts)
            : null;
        notification.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record MarkNotificationFailedRequest(string? Error);

    [HttpGet("failed")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetFailed(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(new GetFailedNotificationsQuery(limit), cancellationToken));

    [HttpPost("{notificationId:guid}/retry")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Retry(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediator.Send(
                new RetryFailedNotificationCommand(notificationId),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { Message = exception.Message });
        }
    }

    public sealed record NotificationResponse(
        Guid Id,
        string EventType,
        string Recipient,
        string Payload,
        int Attempts,
        DateTime CreatedAt);

    public sealed record NotificationStatusResponse(
        Guid Id,
        string Channel,
        string EventType,
        string Status,
        int Attempts,
        string? LastError,
        DateTime CreatedAt,
        DateTime? ProcessedAt);
}
