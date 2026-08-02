using System.Text.Json;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Notifications;

public static class N8nIntegrationEventFactory
{
    public static NotificationOutbox Create(
        Order order,
        string eventType,
        object payload,
        DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            CustomerId = order.CustomerId,
            OrderId = order.Id,
            Channel = "N8n",
            EventType = eventType,
            Recipient = "n8n",
            Payload = JsonSerializer.Serialize(payload)
        };
}
