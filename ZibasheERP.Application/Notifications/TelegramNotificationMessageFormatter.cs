using System.Text.Json;

namespace ZibasheERP.Application.Notifications;

public static class TelegramNotificationMessageFormatter
{
    public static string Format(string eventType, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (eventType == "DebtReminder")
        {
            var message = ReadString(root, "Message") ?? "لطفاً برای تسویه اقدام کنید.";
            return $"یادآوری زیباشه: مانده بدهی شما {ReadDecimal(root, "Amount"):N0} تومان است. {message}";
        }
        if (eventType == "TelegramGroupDeliveryTest")
        {
            return ReadString(root, "Message") ??
                "✅ اتصال گروه به سامانه زیباشه با موفقیت آزمایش شد.";
        }
        var orderNumber = ReadString(root, "OrderNumber") ?? "نامشخص";

        return eventType switch
        {
            "OrderPaid" => $"پرداخت سفارش {orderNumber} با موفقیت تأیید شد.",
            "InvoiceIssued" => $"فاکتور {ReadString(root, "InvoiceNumber") ?? string.Empty} برای سفارش {orderNumber} به مبلغ {ReadDecimal(root, "TotalAmount"):N0} تومان صادر شد.",
            "OrderDecanted" => $"دکانت سفارش {orderNumber} انجام شد و سفارش در حال آماده‌سازی است.",
            "OrderReadyToShip" => $"سفارش {orderNumber} آماده ارسال است.",
            "OrderCancelled" => $"سفارش {orderNumber} لغو شد. علت: {ReadString(root, "Reason") ?? "ثبت نشده"}",
            "OrderShipped" => FormatShipped(root, orderNumber),
            "OrderDelivered" => $"سفارش {orderNumber} تحویل داده شد. از خرید شما سپاسگزاریم.",
            "PaymentRejected" => $"پرداخت سفارش {orderNumber} تأیید نشد. علت: {ReadString(root, "Reason") ?? "نیازمند بررسی"}",
            "PaymentRefunded" => $"مبلغ {ReadDecimal(root, "Amount"):N0} تومان برای سفارش {orderNumber} بازپرداخت شد. علت: {ReadString(root, "Reason") ?? "ثبت نشده"}",
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

    private static decimal ReadDecimal(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var result)
            ? result
            : 0;
}
