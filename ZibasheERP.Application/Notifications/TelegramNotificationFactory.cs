using System.Text.Json;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Notifications;

public static class TelegramNotificationFactory
{
    public static NotificationOutbox? Create(
        Customer customer,
        string eventType,
        object payload,
        DateTime createdAt)
    {
        var telegramId = customer.TelegramId?.Trim();
        if (string.IsNullOrWhiteSpace(telegramId))
            return null;

        return new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            CustomerId = customer.Id,
            EventType = eventType,
            Recipient = telegramId,
            Payload = JsonSerializer.Serialize(payload)
        };
    }

    public static NotificationOutbox? Create(
        Order order,
        string eventType,
        object payload,
        DateTime createdAt)
    {
        var telegramId = order.Customer?.TelegramId?.Trim();
        if (string.IsNullOrWhiteSpace(telegramId))
            return null;

        return new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            CustomerId = order.CustomerId,
            OrderId = order.Id,
            EventType = eventType,
            Recipient = telegramId,
            Payload = JsonSerializer.Serialize(payload)
        };
    }
}
