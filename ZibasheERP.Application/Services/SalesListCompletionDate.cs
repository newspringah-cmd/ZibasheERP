using System.Globalization;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Services;

public static class SalesListCompletionDate
{
    public static string Format(SalesList list, IReadOnlyCollection<SalesListRequest> requests)
    {
        var completedAt = list.ClosedDate;
        if (!completedAt.HasValue && list.ReservedVolume >= list.TotalVolume && list.TotalVolume > 0)
            completedAt = requests.Where(request => !request.IsDeleted && request.Kind == SalesListRequestKind.CurrentBottle &&
                request.Status is SalesListRequestStatus.Confirmed or SalesListRequestStatus.Promoted or
                    SalesListRequestStatus.QueuedForInvoice or SalesListRequestStatus.Invoiced)
                .Select(request => request.ConfirmedAt).DefaultIfEmpty(null).Max();
        if (!completedAt.HasValue) return "ثبت نشده";
        var utc = completedAt.Value.Kind == DateTimeKind.Local ? completedAt.Value.ToUniversalTime() :
            DateTime.SpecifyKind(completedAt.Value, DateTimeKind.Utc);
        var tehran = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Asia/Tehran");
        var calendar = new PersianCalendar();
        var value = FormattableString.Invariant($"{calendar.GetYear(tehran):0000}/{calendar.GetMonth(tehran):00}/{calendar.GetDayOfMonth(tehran):00}");
        return string.Concat(value.Select(character => character is >= '0' and <= '9' ? "۰۱۲۳۴۵۶۷۸۹"[character - '0'] : character));
    }
}
