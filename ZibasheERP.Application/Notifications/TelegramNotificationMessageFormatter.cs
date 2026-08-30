using System.Text;
using System.Text.Json;
using System.Globalization;

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
        if (eventType is "TelegramCustomerGroupRequired" or "InvoiceDeliveryRequiresManualAction")
        {
            var username = ReadString(root, "Username");
            var usernameText = string.IsNullOrWhiteSpace(username)
                ? "ثبت نشده"
                : $"@{username.Trim().TrimStart('@')}";
            var invoiceNumber = ReadString(root, "InvoiceNumber") ?? "نامشخص";
            return $"⚠️ فاکتور نیازمند اقدام حسابدار\n" +
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
        var username = ReadString(root, "CustomerUsername");
        var title = string.IsNullOrWhiteSpace(username)
            ? "فاکتور عطر"
            : $"فاکتور عطر — @{username.Trim().TrimStart('@')}";
        var builder = new StringBuilder()
            .AppendLine($"🧾 {title}")
            .AppendLine($"تاریخ شمسی: {FormatPersianDate(ReadDateTime(root, "IssuedAt"))}")
            .AppendLine($"شماره فاکتور: {ReadString(root, "InvoiceNumber") ?? "نامشخص"}")
            .AppendLine($"شماره سفارش: {orderNumber}")
            .AppendLine("وضعیت پرداخت: ⏳ در انتظار پرداخت")
            .AppendLine();

        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var rowNumber = ReadInt(item, "RowNumber");
                var brand = ReadString(item, "PerfumeBrand");
                var englishName = ReadString(item, "PerfumeEnglishName") ?? ReadString(item, "PerfumeName");
                var persianName = ReadString(item, "PerfumePersianName") ?? ReadString(item, "PerfumeName");
                var englishTitle = string.Join(' ', new[] { brand, englishName }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                if (string.IsNullOrWhiteSpace(englishTitle))
                    englishTitle = "آیتم دستی";
                if (string.IsNullOrWhiteSpace(persianName))
                    persianName = "آیتم دستی";

                builder
                    .AppendLine($"{rowNumber}.")
                    .AppendLine($"نام انگلیسی: {englishTitle}")
                    .AppendLine($"نام فارسی: {persianName}")
                    .AppendLine($"مقدار: {ReadInt(item, "RequestedVolumeMl")} میلی‌لیتر")
                    .AppendLine($"مبلغ عطر و شیشه: {ReadDecimal(item, "LineTotal"):N0} تومان")
                    .AppendLine();

                if (builder.Length > 3200)
                {
                    builder.AppendLine("… ادامه ردیف‌ها در نسخه PDF");
                    break;
                }
            }

        }

        builder
            .AppendLine($"💰 جمع عطر و شیشه: {ReadDecimal(root, "TotalAmount"):N0} تومان")
            .AppendLine();

        if (root.TryGetProperty("PaymentAccounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
        {
            builder.AppendLine("شماره کارت جهت واریز:");
            foreach (var account in accounts.EnumerateArray())
            {
                builder.AppendLine(FormatCard(ReadString(account, "CardNumber") ?? string.Empty));
                builder.AppendLine($"{ReadString(account, "AccountHolder")} — بانک {ReadString(account, "BankName")}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("با تشکر از خرید شما")
            .AppendLine("مهلت پرداخت فاکتور: ۲۴ ساعت")
            .Append("📎 فایل PDF همراه همین فاکتور ارسال می‌شود.");

        return builder.ToString();
    }

    private static string FormatCard(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 16
            ? string.Join('-', Enumerable.Range(0, 4).Select(i => digits.Substring(i * 4, 4)))
            : value;
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

    private static DateTime? ReadDateTime(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTime(out var result)
            ? result
            : null;

    private static string FormatPersianDate(DateTime? value)
    {
        if (!value.HasValue)
            return "نامشخص";

        var utc = value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : value.Value.ToUniversalTime();
        DateTime tehran;
        try
        {
            tehran = TimeZoneInfo.ConvertTimeFromUtc(
                utc,
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran"));
        }
        catch (TimeZoneNotFoundException)
        {
            tehran = utc.AddHours(3.5);
        }
        catch (InvalidTimeZoneException)
        {
            tehran = utc.AddHours(3.5);
        }

        var calendar = new PersianCalendar();
        return $"{calendar.GetYear(tehran):0000}/{calendar.GetMonth(tehran):00}/{calendar.GetDayOfMonth(tehran):00} " +
               $"ساعت {tehran:HH:mm}";
    }
}
