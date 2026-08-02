using ZibasheERP.Application.Features.Notifications.ManageFailedNotifications;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Features.Notifications;

public sealed class FailedNotificationManagementTests
{
    [Fact]
    public async Task RetryFailedNotification_ResetsAndQueuesNotification()
    {
        var notification = new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            EventType = "OrderPaid",
            Recipient = "123456789",
            Status = NotificationOutboxStatus.Failed,
            Attempts = 5,
            LastError = "Telegram timeout"
        };
        var repository = new AdminNotificationRepositoryStub(notification);
        var handler = new RetryFailedNotificationCommandHandler(repository);

        var result = await handler.Handle(
            new RetryFailedNotificationCommand(notification.Id),
            CancellationToken.None);

        Assert.Equal(NotificationOutboxStatus.Pending, notification.Status);
        Assert.Equal(0, notification.Attempts);
        Assert.Null(notification.LastError);
        Assert.Equal("Pending", result.Status);
        Assert.True(repository.SaveChangesCalled);
    }

    [Fact]
    public async Task RetryFailedNotification_RejectsProcessedNotification()
    {
        var notification = new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            Status = NotificationOutboxStatus.Processed
        };
        var handler = new RetryFailedNotificationCommandHandler(
            new AdminNotificationRepositoryStub(notification));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(
                new RetryFailedNotificationCommand(notification.Id),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetFailedNotifications_ReturnsOperationalDetails()
    {
        var notification = new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            EventType = "DebtReminder",
            Recipient = "123456789",
            Status = NotificationOutboxStatus.Failed,
            Attempts = 5,
            LastError = "Blocked by user"
        };
        var handler = new GetFailedNotificationsQueryHandler(
            new AdminNotificationRepositoryStub(notification));

        var result = await handler.Handle(
            new GetFailedNotificationsQuery(),
            CancellationToken.None);

        Assert.Equal(1, result.Count);
        Assert.Equal("Blocked by user", result.Single().LastError);
    }

    private sealed class AdminNotificationRepositoryStub(params NotificationOutbox[] notifications)
        : IAdminNotificationRepository
    {
        public bool SaveChangesCalled { get; private set; }
        public Task<IReadOnlyCollection<NotificationOutbox>> GetFailedAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<NotificationOutbox>>(
                notifications.Where(item => item.Status == NotificationOutboxStatus.Failed).Take(limit).ToArray());
        public Task<NotificationOutbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(notifications.FirstOrDefault(item => item.Id == id));
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalled = true;
            return Task.CompletedTask;
        }
    }
}
