using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Notifications.ManageFailedNotifications;

public sealed class GetFailedNotificationsQueryHandler
    : IRequestHandler<GetFailedNotificationsQuery, IReadOnlyCollection<FailedNotificationResponse>>
{
    private readonly IAdminNotificationRepository _repository;
    public GetFailedNotificationsQueryHandler(IAdminNotificationRepository repository) => _repository = repository;

    public async Task<IReadOnlyCollection<FailedNotificationResponse>> Handle(
        GetFailedNotificationsQuery request,
        CancellationToken cancellationToken)
    {
        var notifications = await _repository.GetFailedAsync(
            Math.Clamp(request.Limit, 1, 100), cancellationToken);
        return notifications.Select(FailedNotificationMapper.ToResponse).ToArray();
    }
}

public sealed class RetryFailedNotificationCommandHandler
    : IRequestHandler<RetryFailedNotificationCommand, FailedNotificationResponse>
{
    private readonly IAdminNotificationRepository _repository;
    public RetryFailedNotificationCommandHandler(IAdminNotificationRepository repository) => _repository = repository;

    public async Task<FailedNotificationResponse> Handle(
        RetryFailedNotificationCommand request,
        CancellationToken cancellationToken)
    {
        var notification = await _repository.GetByIdAsync(request.NotificationId, cancellationToken)
            ?? throw new InvalidOperationException("اعلان پیدا نشد.");
        if (notification.Status != NotificationOutboxStatus.Failed)
            throw new InvalidOperationException("فقط اعلان ناموفق قابل ارسال مجدد است.");

        notification.Status = NotificationOutboxStatus.Pending;
        notification.Attempts = 0;
        notification.LastError = null;
        notification.ProcessedAt = null;
        notification.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(cancellationToken);
        return FailedNotificationMapper.ToResponse(notification);
    }
}

internal static class FailedNotificationMapper
{
    internal static FailedNotificationResponse ToResponse(NotificationOutbox notification) => new(
        notification.Id,
        notification.CustomerId,
        notification.OrderId,
        notification.EventType,
        notification.Recipient,
        notification.Status.ToString(),
        notification.Attempts,
        notification.LastError,
        notification.CreatedAt,
        notification.UpdatedAt);
}
