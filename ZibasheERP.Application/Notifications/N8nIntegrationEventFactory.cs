using System.Text.Json;
using System.Text.Json.Nodes;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Notifications;

public static class N8nIntegrationEventFactory
{
    public static NotificationOutbox Create(
        Order order,
        string eventType,
        object payload,
        DateTime createdAt)
    {
        var body = JsonSerializer.SerializeToNode(payload) as JsonObject
            ?? throw new InvalidOperationException("N8n event payload must be a JSON object.");
        body["Delivery"] = CreateDelivery(order.Customer);

        return new NotificationOutbox
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            CustomerId = order.CustomerId,
            OrderId = order.Id,
            Channel = "N8n",
            EventType = eventType,
            Recipient = "n8n",
            Payload = body.ToJsonString()
        };
    }

    private static JsonNode? CreateDelivery(Customer? customer)
    {
        var group = customer?.TelegramGroup;
        if (group is null || group.IsDeleted || !group.IsActive ||
            string.IsNullOrWhiteSpace(group.ChatId))
        {
            return null;
        }

        return JsonSerializer.SerializeToNode(new
        {
            Channel = "TelegramGroup",
            ChatId = group.ChatId.Trim(),
            group.Title,
            group.Username
        });
    }
}
