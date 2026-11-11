using System.Globalization;
using System.Net;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Marketing;

public static class HourlyPromotionCopy
{
    public static string Number(decimal value, string format = "0.##") => string.Concat(
        value.ToString(format, CultureInfo.InvariantCulture).Replace(',', '٬').Select(c => c is >= '0' and <= '9' ? "۰۱۲۳۴۵۶۷۸۹"[c - '0'] : c));
    private static string Name(SalesList list) => WebUtility.HtmlEncode(
        string.IsNullOrWhiteSpace(list.EnglishName) ? list.PersianName : list.EnglishName) +
        (string.IsNullOrWhiteSpace(list.DisplayBrand) ? "" : " · " + WebUtility.HtmlEncode(list.DisplayBrand));
    public static string Discount(FlashSalePromotion promotion) => promotion.DiscountAmountPerMl.HasValue
        ? Number(promotion.DiscountAmountPerMl.Value, "#,0.##") + " تومان تخفیف روی هر میل عطر"
        : Number(promotion.DiscountPercent) + "٪ تخفیف روی مبلغ عطر";
    public static string Caption(FlashSalePromotion promotion, SalesList list, DateTime now)
    {
        if (!promotion.IsActiveAt(now)) return EndMessage(promotion, list);
        var remaining = Math.Max(0, (int)Math.Ceiling((promotion.EndsAtUtc - now).TotalMinutes));
        var timer = Number(remaining / 60, "00") + ":" + Number(remaining % 60, "00");
        var label = Number(promotion.RequestedDurationMinutes / 60m) + " ساعت";
        var headline=$"{label} فرصت خرید عطر با تخفیف ویژه!";
        if((promotion.EndsAtUtc-promotion.StartsAtUtc).TotalMinutes>promotion.RequestedDurationMinutes+1)
        {
            var localEnd=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(promotion.EndsAtUtc,DateTimeKind.Utc),"Asia/Tehran");
            headline=$"فرصت خرید عطر با تخفیف ویژه تا ساعت {Number(localEnd.Hour,"00")}:{Number(localEnd.Minute,"00")} روز {ZibasheERP.API.AddressLabels.ShippingAddressDisplay.RegisteredDate(promotion.EndsAtUtc)}";
        }
        return $"⚡️ <b>{headline}</b>\n\nزیباشی‌های عزیزم 🌸\n" +
            $"این بار نوبت <b>{Name(list)}</b> شده 🤍\n\n🎁 <b>{Discount(promotion)}</b>\n" +
            $"💰 قیمت عادی هر میل: {Number(promotion.OriginalPricePerMl, "#,0.##")} تومان\n" +
            $"💰 <b>قیمت هر میل با تخفیف: {Number(promotion.DiscountedPricePerMl, "#,0.##")} تومان</b>\n\n" +
            $"⏳ <b>زمان باقی‌مانده: {timer}</b>\n" +
            "برای استفاده از تخفیف، ثبت نهایی آیتم باید قبل از پایان مهلت انجام بشه.\n\n" +
            $"📋 {(list.IsInventoryOffer?"کد موجودی":"کد لیست")}: <b>{Number(list.DisplayCode)}</b>\n" +
            (list.IsInventoryOffer ? $"📦 {list.TotalVolume} میل با شیشه: {Number(promotion.DiscountedPricePerMl*list.TotalVolume+(list.FixedBottlePrice??0),"#,0.##")} تومان\n" : "") +
            (list.RemainingVolume > 0 ? "👇 از دکمهٔ زیر لیست رو ببین و خریدت رو ثبت کن ✨" : list.IsInventoryOffer?"این بسته فعلاً رزرو شده یا فروخته شده است؛ امکان خرید هم‌زمان ندارد.":"این باتل تکمیل شده؛ فرصت تخفیف تا پایان زمان اعلام‌شده روی چرخهٔ بعدی همین لیست ادامه دارد.");
    }
    public static string EndMessage(FlashSalePromotion promotion, SalesList list) =>
        $"🎉 <b>پایان پروموشن ساعتی {Name(list)}</b>\n\nزیباشی‌های عزیزم 🌸\n" +
        "این پروموشن به پایان رسید. از همراهی، اعتماد و خریدهای قشنگتون خیلی ممنونیم 🤍🌷\n\n" +
        "✅ تخفیف آیتم‌هایی که تا پایان پروموشن ثبت نهایی شدن، توی فاکتورشون محفوظ می‌مونه.\n\n" +
        "ان‌شاءالله در آینده هم از این تخفیف‌های خوش‌عطر براتون خواهیم داشت؛ منتظر فرصت‌های بعدی باشین ✨💛";
}
