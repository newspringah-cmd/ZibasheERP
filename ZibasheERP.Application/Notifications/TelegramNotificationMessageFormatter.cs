using System.Text;
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
            return $"یادآوری زیباشی: مانده بدهی شما {ReadDecimal(root, "Amount"):N0} تومان است. {message}";
        }
        if (eventType == "TelegramGroupDeliveryTest")
        {
            return ReadString(root, "Message") ??
                "✅ اتصال گروه به سامانه زیباشی با موفقیت آزمایش شد.";
        }
        if (eventType == "TelegramGroupDeliveryFailed")
        {
            return $"⚠️ هشدار ارسال گروه زیباشی\n" +
                   $"شناسه مشتری: {ReadString(root, "CustomerId") ?? "نامشخص"}\n" +
                   $"شناسه گروه: {ReadString(root, "GroupChatId") ?? "نامشخص"}\n" +
                   $"شناسه اعلان: {ReadString(root, "NotificationId") ?? "نامشخص"}\n" +
                   $"خطا: {ReadString(root, "Error") ?? "ثبت نشده"}";
        }
        if (eventType == "TelegramCustomerGroupRequired")
        {
            var username = ReadString(root, "Username");
            var usernameText = string.IsNullOrWhiteSpace(username)
                ? "ثبت نشده"
                : $"@{username.Trim().TrimStart('@')}";
            var invoiceNumber = ReadString(root, "InvoiceNumber") ?? "نامشخص";
            return $"🆕 مشتری جدید بدون گروه حسابداری\n" +
                   $"نام مشتری: {ReadString(root, "FullName") ?? "نامشخص"}\n" +
                   $"Username: {usernameText}\n" +
                   $"شماره سفارش: {ReadString(root, "OrderNumber") ?? "نامشخص"}\n" +
                   $"شماره فاکتور: {invoiceNumber}\n\n" +
                   $"لطفاً گروه مشتری را بسازید، ربات را اضافه کنید و داخل گروه بفرستید:\n" +
                   $"/connect {invoiceNumber}";
        }
        var orderNumber = ReadString(root, "OrderNumber") ?? "نامشخص";

        return eventType switch
        {
            "OrderPaid" => $"پرداخت سفارش {orderNumber} با موفقیت تأیید شد.",
            "InvoiceIssued" => FormatInvoice(root, orderNumber),
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

    private static string FormatInvoice(JsonElement root, string orderNumber)
    {
        var builder = new StringBuilder()
            .AppendLine("🧾 فاکتور فروش زیباشی")
            .AppendLine($"شماره فاکتور: {ReadString(root, "InvoiceNumber") ?? "نامشخص"}")
            .AppendLine($"شماره سفارش: {orderNumber}")
            .AppendLine();

        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var rowNumber = ReadInt(item, "RowNumber");
                var brand = ReadString(item, "PerfumeBrand");
                var name = ReadString(item, "PerfumeName");
                var perfume = string.Join(' ', new[] { brand, name }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (string.IsNullOrWhiteSpace(perfume))
                    perfume = "عطر";

                builder.AppendLine(
                    $"{rowNumber}. {perfume} — {ReadInt(item, "RequestedVolumeMl")} میلی‌لیتر — " +
                    $"{ReadDecimal(item, "LineTotal"):N0} تومان");

                if (builder.Length > 3200)
                {
                    builder.AppendLine("… ادامه ردیف‌ها در نسخه PDF");
                    break;
                }
            }

            builder.AppendLine();
        }

        builder
            .AppendLine($"جمع عطر: {ReadDecimal(root, "PerfumeTotal"):N0} تومان")
            .AppendLine($"جمع شیشه: {ReadDecimal(root, "BottleTotal"):N0} تومان")
            .AppendLine($"مبلغ نهایی: {ReadDecimal(root, "TotalAmount"):N0} تومان")
            .AppendLine()
            .Append("نسخه PDF همین فاکتور نیز در گروه ارسال می‌شود.");

        return builder.ToString();
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

    private static int ReadInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
}
