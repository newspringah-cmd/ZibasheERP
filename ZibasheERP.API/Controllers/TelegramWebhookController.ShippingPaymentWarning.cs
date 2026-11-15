using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string ShippingUnpaidInvoiceWarning=
        "⚠️ فاکتور پرداخت‌نشده باقی مانده است.\nلطفاً به مشتری اطلاع دهید پیش از ارسال پرداخت شود.";

    private Task<bool> HasUnpaidShippingInvoicesAsync(Guid customerId,CancellationToken ct)=>
        _db.Invoices.AsNoTracking().AnyAsync(invoice=>!invoice.IsDeleted&&invoice.Status==InvoiceStatus.Issued&&
            invoice.Order!=null&&!invoice.Order.IsDeleted&&invoice.Order.Status!=OrderStatus.Cancelled&&
            invoice.Order.CustomerId==customerId&&invoice.Order.Customer!=null&&!invoice.Order.Customer.IsDeleted,ct);

    private async Task AnswerShippingPreparationCallbackAsync(TelegramCallbackQuery callback,
        Guid? customerId,CancellationToken ct,string? success=null)
    {
        // Callback alerts are visible only to the operator who clicked. Do not post
        // a customer's outstanding balance into a shared group or forward it to another user.
        if(IsAuthorizedShippingOperator(callback.From.Id))
        {
            if(!customerId.HasValue&&callback.Message is not null)
            {
                var links=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&
                    !g.Customer.IsDeleted&&g.ChatId==callback.Message.Chat.Id.ToString())
                    .Select(g=>new{g.CustomerId,g.IsPrimaryForShipping}).ToArrayAsync(ct);
                var primary=links.Where(g=>g.IsPrimaryForShipping).ToArray();
                customerId=links.Length==1?links[0].CustomerId:primary.Length==1?primary[0].CustomerId:null;
            }
            // Re-read at final dispatch, even if no debt existed when address entry began.
            if(customerId.HasValue&&await HasUnpaidShippingInvoicesAsync(customerId.Value,ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id,ShippingUnpaidInvoiceWarning,ct,true);
                return;
            }
        }
        await _sender.AnswerCallbackAsync(callback.Id,success,ct,success is not null);
    }
}
