using System.Text.Json;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class N8nIntegrationEventFactoryTests
{
    [Fact]
    public void Create_UsesDedicatedChannelAndOrderIdentity()
    {
        var order = new Order { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid() };
        var createdAt = DateTime.UtcNow;

        var result = N8nIntegrationEventFactory.Create(
            order,
            "InvoiceIssued",
            new { InvoiceNumber = "INV-1" },
            createdAt);

        Assert.Equal("N8n", result.Channel);
        Assert.Equal("n8n", result.Recipient);
        Assert.Equal(order.Id, result.OrderId);
        Assert.Equal(order.CustomerId, result.CustomerId);
        Assert.Equal("InvoiceIssued", result.EventType);
        using var payload = JsonDocument.Parse(result.Payload);
        Assert.Equal("INV-1", payload.RootElement.GetProperty("InvoiceNumber").GetString());
    }
}
