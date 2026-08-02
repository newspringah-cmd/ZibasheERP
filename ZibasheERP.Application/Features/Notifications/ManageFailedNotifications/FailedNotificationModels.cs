using MediatR;

namespace ZibasheERP.Application.Features.Notifications.ManageFailedNotifications;

public sealed record GetFailedNotificationsQuery(int Limit = 50)
    : IRequest<IReadOnlyCollection<FailedNotificationResponse>>;

public sealed record RetryFailedNotificationCommand(Guid NotificationId)
    : IRequest<FailedNotificationResponse>;

public sealed record FailedNotificationResponse(
    Guid Id,
    Guid CustomerId,
    Guid? OrderId,
    string EventType,
    string Recipient,
    string Status,
    int Attempts,
    string? LastError,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
