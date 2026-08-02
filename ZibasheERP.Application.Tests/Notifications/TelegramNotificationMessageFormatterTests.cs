using ZibasheERP.Application.Notifications;
using Xunit;

namespace ZibasheERP.Application.Tests.Notifications;

public sealed class TelegramNotificationMessageFormatterTests
{
    [Fact]
    public void Format_GroupDeliveryTest_ReturnsSafeOperationalMessage()
    {
        var result = TelegramNotificationMessageFormatter.Format(
            "TelegramGroupDeliveryTest",
            "{\"Message\":\"اتصال آزمایشی زیباشه\"}");

        Assert.Equal("اتصال آزمایشی زیباشه", result);
    }

    [Fact]
    public void Format_OrderPaid_IncludesOrderNumber()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "OrderPaid",
            "{\"OrderNumber\":\"ZS-1001\"}");

        Assert.Contains("ZS-1001", message);
        Assert.Contains("تأیید", message);
    }

    [Fact]
    public void Format_OrderShipped_IncludesCarrierAndTrackingCode()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "OrderShipped",
            "{\"OrderNumber\":\"ZS-1002\",\"ShippingCompany\":\"Post\",\"TrackingCode\":\"TRACK-42\"}");

        Assert.Contains("Post", message);
        Assert.Contains("TRACK-42", message);
    }

    [Fact]
    public void Format_OrderDelivered_IncludesOrderNumber()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "OrderDelivered",
            "{\"OrderNumber\":\"ZS-1003\"}");

        Assert.Contains("ZS-1003", message);
        Assert.Contains("تحویل", message);
    }

    [Fact]
    public void Format_PaymentRejected_IncludesReason()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "PaymentRejected",
            "{\"OrderNumber\":\"ZS-1004\",\"Reason\":\"Bank reference not found\"}");

        Assert.Contains("ZS-1004", message);
        Assert.Contains("Bank reference not found", message);
    }

    [Fact]
    public void Format_InvoiceIssued_IncludesInvoiceAndAmount()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "InvoiceIssued",
            "{\"OrderNumber\":\"ZS-1005\",\"InvoiceNumber\":\"INV-42\",\"TotalAmount\":1250000}");

        Assert.Contains("INV-42", message);
        Assert.Contains("1,250,000", message);
    }

    [Fact]
    public void Format_OrderReadyToShip_IncludesOrderNumber()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "OrderReadyToShip",
            "{\"OrderNumber\":\"ZS-1006\"}");

        Assert.Contains("ZS-1006", message);
        Assert.Contains("آماده ارسال", message);
    }

    [Fact]
    public void Format_OrderCancelled_IncludesReason()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "OrderCancelled",
            "{\"OrderNumber\":\"ZS-1007\",\"Reason\":\"Customer request\"}");

        Assert.Contains("ZS-1007", message);
        Assert.Contains("Customer request", message);
    }

    [Fact]
    public void Format_PaymentRefunded_IncludesOrderAmountAndReason()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "PaymentRefunded",
            "{\"OrderNumber\":\"ZS-1008\",\"Amount\":750000,\"Reason\":\"Customer request\"}");

        Assert.Contains("ZS-1008", message);
        Assert.Contains("750,000", message);
        Assert.Contains("Customer request", message);
    }

    [Fact]
    public void Format_DebtReminder_IncludesAmountAndCustomMessage()
    {
        var message = TelegramNotificationMessageFormatter.Format(
            "DebtReminder",
            "{\"Amount\":750000,\"Message\":\"Please settle\"}");

        Assert.Contains("750,000", message);
        Assert.Contains("Please settle", message);
    }
}
