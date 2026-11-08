using System.Net;
using ZibasheERP.Domain.Entities;
namespace ZibasheERP.API.Inventory;
public static class ReadyInventoryPresentation
{
    public static string Caption(SalesList list) =>
        $"🧴 <b>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(list.PersianName)?list.EnglishName:list.PersianName)}</b>\n"+
        $"🔖 کد موجودی: <b>{list.DisplayCode}</b>\n📦 حجم: {list.TotalVolume:0.#} میل — آمادهٔ ارسال\n"+
        $"💰 قیمت هر میل عطر: {list.PricePerMl:N0} تومان\n💰 مبلغ عطر و شیشه: {list.PricePerMl*list.TotalVolume+(list.FixedBottlePrice??0):N0} تومان\n\n"+
        (string.IsNullOrWhiteSpace(list.InventoryPublicDescription)?"":$"🔴 <b>توضیحات: {WebUtility.HtmlEncode(list.InventoryPublicDescription)}</b>\n\n")+
        (list.Status==SalesListStatus.Open&&list.RemainingVolume==list.TotalVolume?"✅ موجود؛ برای درخواست خرید دکمهٔ زیر را بزنید.\nفاکتور پس از بررسی ادمین صادر می‌شود.":
         list.Status is SalesListStatus.Full?"⏳ رزرو شده؛ در انتظار بررسی ادمین.":list.Status==SalesListStatus.Cancelled?"این موجودی از فروش خارج شده است.":"✅ فروخته شده؛ امکان خرید مجدد ندارد.");
}
