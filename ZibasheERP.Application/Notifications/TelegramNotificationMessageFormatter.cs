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
            return $"یادآوری زیباشی: مانده بدهی شما {FormatAmount(ReadDecimal(root, "Amount"))} تومان است. {message}";
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
        if (eventType == "InvoiceGiftDeliveryRequiresManualAction")
        {
            var recipientUsername = ReadString(root, "RecipientUsername");
            var recipient = string.IsNullOrWhiteSpace(recipientUsername)
                ? ReadString(root, "RecipientTelegramId") ?? "نامشخص"
                : $"@{recipientUsername.Trim().TrimStart('@')}";
            var giverUsername = ReadString(root, "GiverUsername");
            var giver = string.IsNullOrWhiteSpace(giverUsername)
                ? ReadString(root, "GiverTelegramId") ?? "نامشخص"
                : $"@{giverUsername.Trim().TrimStart('@')}";
            return $"⚠️ فاکتور هدیه به گیرنده ارسال نشد\n" +
                   $"گیرنده: {recipient}\n" +
                   $"هدیه‌دهنده: {giver}\n" +
                   $"شماره فاکتور مالی: {ReadString(root, "InvoiceNumber") ?? "نامشخص"}\n\n" +
                   "گروه فعال هدیه‌گیرنده شناسایی نشد؛ ربات را به گروه اضافه کنید و داخل همان گروه این فرمان را ارسال کنید:\n" +
                   $"/connectgift {ReadString(root, "InvoiceNumber") ?? "نامشخص"} {recipient.TrimStart('@')}";
        }
        var orderNumber = ReadString(root, "OrderNumber") ?? "نامشخص";

        return eventType switch
        {
            "OrderPaid" => $"پرداخت سفارش {orderNumber} با موفقیت تأیید شد.",
            "InvoiceIssued" => FormatInvoice(root, orderNumber),
            "GiftInvoiceIssued" => FormatGiftInvoice(root),
            "OrderDecanted" => $"دکانت سفارش {orderNumber} انجام شد و سفارش در حال آماده‌سازی است.",
            "OrderReadyToShip" => $"سفارش {orderNumber} آماده ارسال است.",
            "OrderCancelled" => $"سفارش {orderNumber} لغو شد. علت: {ReadString(root, "Reason") ?? "ثبت نشده"}",
            "OrderShipped" => FormatShipped(root, orderNumber),
            "OrderDelivered" => $"سفارش {orderNumber} تحویل داده شد. از خرید شما سپاسگزاریم.",
            "PaymentRejected" => $"پرداخت سفارش {orderNumber} تأیید نشد. علت: {ReadString(root, "Reason") ?? "نیازمند بررسی"}",
            "PaymentRefunded" => $"مبلغ {FormatAmount(ReadDecimal(root, "Amount"))} تومان برای سفارش {orderNumber} بازپرداخت شد. علت: {ReadString(root, "Reason") ?? "ثبت نشده"}",
            _ => $"وضعیت سفارش {orderNumber} به‌روزرسانی شد."
        };
    }

    private static string FormatInvoice(JsonElement root, string orderNumber)
    {
        var username = ReadString(root, "CustomerUsername");
        if (string.IsNullOrWhiteSpace(username) &&
            root.TryGetProperty("Customer", out var customer) &&
            customer.ValueKind == JsonValueKind.Object)
        {
            username = ReadString(customer, "Username");
        }
        var title = string.IsNullOrWhiteSpace(username)
            ? "فاکتور عطر"
            : $"فاکتور عطر — @{username.Trim().TrimStart('@')}";
        var invoiceItems = root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var hasGiftItems = invoiceItems.Any(item => ReadBoolean(item, "IsGift"));
        var builder = new StringBuilder();
        if (hasGiftItems)
        {
            AppendSpacedLine(builder, $"🧾 {title}");
            AppendSpacedLine(builder, $"تاریخ شمسی: {FormatPersianDate(ReadDateTime(root, "IssuedAt"))}");
            AppendSpacedLine(builder, $"شماره فاکتور: {ReadString(root, "InvoiceNumber") ?? "نامشخص"}");
            AppendSpacedLine(builder, $"شماره سفارش: {orderNumber}");
        }
        else
        {
            builder
                .AppendLine($"🧾 {title}")
                .AppendLine($"تاریخ شمسی: {FormatPersianDate(ReadDateTime(root, "IssuedAt"))}")
                .AppendLine($"شماره فاکتور: {ReadString(root, "InvoiceNumber") ?? "نامشخص"}")
                .AppendLine($"شماره سفارش: {orderNumber}")
                .AppendLine();
        }

        foreach (var item in invoiceItems.Where(item => ReadBoolean(item, "IsGift")))
        {
            var recipient = ReadString(item, "GiftRecipientUsername");
            recipient = string.IsNullOrWhiteSpace(recipient)
                ? ReadString(item, "GiftRecipientTelegramId")
                : $"@{recipient.Trim().TrimStart('@')}";
            AppendSpacedLine(builder, $"🎁 هدیه برای: {recipient ?? "گیرنده نامشخص"}");
            AppendInvoiceItemDetails(builder, item, spaced: true);
        }

        foreach (var item in invoiceItems.Where(item => !ReadBoolean(item, "IsGift")))
        {
            if (hasGiftItems) AppendSpacedLine(builder, $"{ReadInt(item, "RowNumber")}.");
            else builder.AppendLine($"{ReadInt(item, "RowNumber")}.");
            AppendInvoiceItemDetails(builder, item, spaced: hasGiftItems);
            if (builder.Length > 3200)
            {
                builder.AppendLine("… ادامه ردیف‌ها در نسخه PDF");
                break;
            }
        }

        if (hasGiftItems)
            AppendSpacedLine(builder, $"💰 جمع عطر و شیشه: {FormatAmount(ReadDecimal(root, "TotalAmount"))} تومان");
        else
            builder.AppendLine($"💰 جمع عطر و شیشه: {FormatAmount(ReadDecimal(root, "TotalAmount"))} تومان").AppendLine();

        if (root.TryGetProperty("PaymentAccounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
        {
            if (hasGiftItems) AppendSpacedLine(builder, "شماره کارت جهت واریز:");
            else builder.AppendLine("شماره کارت جهت واریز:");
            foreach (var account in accounts.EnumerateArray())
            {
                if (hasGiftItems)
                {
                    AppendSpacedLine(builder, FormatCard(ReadString(account, "CardNumber") ?? string.Empty));
                    AppendSpacedLine(builder, $"{ReadString(account, "AccountHolder")} — بانک {ReadString(account, "BankName")}");
                }
                else
                {
                    builder.AppendLine(FormatCard(ReadString(account, "CardNumber") ?? string.Empty));
                    builder.AppendLine($"{ReadString(account, "AccountHolder")} — بانک {ReadString(account, "BankName")}");
                }
            }
            if (!hasGiftItems) builder.AppendLine();
        }

        if (hasGiftItems)
        {
            AppendSpacedLine(builder, "با تشکر از خرید شما");
            builder.Append("مهلت پرداخت فاکتور: ۲۴ ساعت");
        }
        else
        {
            builder.AppendLine("با تشکر از خرید شما")
                .Append("مهلت پرداخت فاکتور: ۲۴ ساعت");
        }

        return builder.ToString();
    }

    private static void AppendInvoiceItemDetails(StringBuilder builder, JsonElement item, bool spaced)
    {
        var brand = ReadString(item, "PerfumeBrand");
        var englishName = ReadString(item, "PerfumeEnglishName") ?? ReadString(item, "PerfumeName");
        var persianName = ReadString(item, "PerfumePersianName") ?? ReadString(item, "PerfumeName");
        var englishTitle = string.Join(' ', new[] { brand, englishName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(englishTitle)) englishTitle = "آیتم دستی";
        if (string.IsNullOrWhiteSpace(persianName)) persianName = "آیتم دستی";

        var lines = new[]
        {
            $"نام انگلیسی: {englishTitle}",
            $"نام فارسی: {persianName}",
            $"مقدار: {ReadInt(item, "RequestedVolumeMl")} میلی‌لیتر",
            $"مبلغ عطر و شیشه: {FormatAmount(ReadDecimal(item, "LineTotal"))} تومان"
        };
        foreach (var line in lines)
        {
            if (spaced) AppendSpacedLine(builder, line);
            else builder.AppendLine(line);
        }
        if (!spaced) builder.AppendLine();
    }

    private static void AppendSpacedLine(StringBuilder builder, string value) =>
        builder.AppendLine(value).AppendLine();

    private static string FormatGiftInvoice(JsonElement root)
    {
        var giver = ReadString(root, "GiverUsername");
        giver = string.IsNullOrWhiteSpace(giver)
            ? ReadString(root, "GiverTelegramId") ?? "کاربر زیباشی"
            : $"@{giver.Trim().TrimStart('@')}";
        return new StringBuilder()
            .AppendLine("🎁 فاکتور هدیه زیباشی")
            .AppendLine($"تاریخ شمسی: {FormatPersianDate(ReadDateTime(root, "IssuedAt"))}")
            .AppendLine($"از طرف: {giver}")
            .AppendLine()
            .AppendLine($"نام انگلیسی: {ReadString(root, "PerfumeEnglishName") ?? "عطر"}")
            .AppendLine($"نام فارسی: {ReadString(root, "PerfumePersianName") ?? "عطر"}")
            .AppendLine($"مقدار: {ReadInt(root, "RequestedVolumeMl")} میلی‌لیتر")
            .AppendLine("مبلغ قابل پرداخت: ۰ تومان")
            .AppendLine()
            .Append("این عطر از طرف هدیه‌دهنده برای شما ثبت شده است 🌹")
            .ToString();
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

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) && root.ValueKind == JsonValueKind.Object)
        {
            value = root.EnumerateObject()
                .FirstOrDefault(property => string.Equals(
                    property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                .Value;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static decimal ReadDecimal(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var result)
            ? result
            : 0;

    private static string FormatAmount(decimal value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static int ReadInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool ReadBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

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
