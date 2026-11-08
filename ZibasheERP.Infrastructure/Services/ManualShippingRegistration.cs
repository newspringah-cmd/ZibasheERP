using System.Globalization;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Infrastructure.Services;

public static class ManualShippingRegistration
{
    public static string? TrackingDestination(Customer customer, string? ownerUserId)
    {
        if (customer.Notes?.StartsWith("manual-address-registration:", StringComparison.Ordinal) != true) return null;
        var parts = customer.Notes.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var registrar) || registrar <= 0 ||
            !Guid.TryParseExact(parts[2], "N", out var registration) || registration != customer.Id)
            throw new InvalidOperationException("مشخصات ثبت دستی آدرس معتبر نیست؛ مقصد کد رهگیری باید بررسی شود.");
        if (!long.TryParse(ownerUserId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner <= 0)
            throw new InvalidOperationException("آیدی مدیر برای دریافت کد رهگیری ثبت‌های دستی تنظیم نشده است.");
        return owner.ToString(CultureInfo.InvariantCulture);
    }
}
