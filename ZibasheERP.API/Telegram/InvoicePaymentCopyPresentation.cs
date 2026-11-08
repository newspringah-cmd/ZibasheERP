using System.Globalization;

namespace ZibasheERP.API.Telegram;

internal static class InvoicePaymentCopyPresentation
{
    internal static TelegramInlineButton? Button(decimal payableTomans)
    {
        if (payableTomans <= 0) return null;
        var rials = decimal.Round(checked(payableTomans * 10m), 0, MidpointRounding.ToEven)
            .ToString("0", CultureInfo.InvariantCulture);
        return new TelegramInlineButton($"📋 کپی مبلغ: {rials} ریال", CopyText: rials);
    }
}
