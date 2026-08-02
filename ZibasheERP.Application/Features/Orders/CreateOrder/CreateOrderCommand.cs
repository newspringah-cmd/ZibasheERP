using MediatR;

namespace ZibasheERP.Application.Features.Orders.CreateOrder;

public class CreateOrderCommand : IRequest<Guid>
{
    // مشتری انتخاب‌شده در پنل ادمین
    public Guid CustomerId { get; set; }

    // برای ثبت سفارش از ربات تلگرام در آینده
    public string? TelegramId { get; set; }

    // لیست فروش
    public Guid SalesListId { get; set; }

    // حجم درخواستی مشتری
    public int RequestedVolumeMl { get; set; }

    // فقط زمانی true می‌شود که ادمین این مشتری را
    // به‌عنوان صاحب باتل اصلی همان لیست تعیین کند
    public bool IsBottleOwner { get; set; }

    // برای صاحب باتل باید null باشد؛
    // برای سفارش عادی، شناسه شیشه معمولی یا فانتزی است
    public Guid? BottleId { get; set; }

    // توضیحات سفارش
    public string? Notes { get; set; }
}