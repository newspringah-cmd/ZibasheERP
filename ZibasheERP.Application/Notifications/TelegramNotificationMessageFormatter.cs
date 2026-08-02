using System.Text.Json;

namespace ZibasheERP.Application.Notifications;

public static class TelegramNotificationMessageFormatter
{
    public static string Format(string eventType, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var orderNumber = ReadString(root, "OrderNumber") ?? "نامشخص";

        return eventType switch
        {
            "OrderPaid" => $"پرداخت سفارش {orderNumber} با موفقیت تأیید شد.",
            "OrderShipped" => FormatShipped(root, orderNumber),
            "OrderDelivered" => $"سفارش {orderNumber} تحویل داده شد. از خرید شما سپاسگزاریم.",
            "PaymentRejected" => $"پرداخت سفارش {orderNumber} تأیید نشد. علت: {ReadString(root, "Reason") ?? "نیازمند بررسی"}",
            _ => $"وضعیت سفارش {orderNumber} به‌روزرسانی شد."
        };
    }

    private static string FormatShipped(JsonElement root, string orderNumber)
    {
        var company = ReadString(root, "ShippingCompany") ?? "شرکت حمل";
        var trackingCode = ReadString(root, "TrackingCode") ?? "ثبت نشده";
        return $"سفارش {orderNumber} با {company} ارسال شد. کد رهگیری: {trackingCode}";
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
