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
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("Delivery").ValueKind);
    }

    [Fact]
    public void Create_WithActiveCustomerGroup_AddsTelegramGroupDelivery()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TelegramGroup = new CustomerTelegramGroup
            {
                ChatId = "-1001234567890",
                Title = "Customer group",
                Username = "customer_group",
                IsActive = true
            }
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer
        };

        var result = N8nIntegrationEventFactory.Create(
            order,
            "OrderDecanted",
            new { order.OrderNumber },
            DateTime.UtcNow);

        using var payload = JsonDocument.Parse(result.Payload);
        var delivery = payload.RootElement.GetProperty("Delivery");
        Assert.Equal("TelegramGroup", delivery.GetProperty("Channel").GetString());
        Assert.Equal("-1001234567890", delivery.GetProperty("ChatId").GetString());
        Assert.Equal("Customer group", delivery.GetProperty("Title").GetString());
    }

    [Fact]
    public void Create_WithInactiveCustomerGroup_DoesNotExposeDelivery()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TelegramGroup = new CustomerTelegramGroup
            {
                ChatId = "-1001234567890",
                Title = "Inactive group",
                IsActive = false
            }
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer
        };

        var result = N8nIntegrationEventFactory.Create(
            order,
            "OrderShipped",
            new { order.OrderNumber },
            DateTime.UtcNow);

        using var payload = JsonDocument.Parse(result.Payload);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("Delivery").ValueKind);
    }
}
