using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string ShippingUnpaidInvoiceWarning=
        "⚠️ فاکتور پرداخت‌نشده باقی مانده است.\nلطفاً به مشتری اطلاع دهید پیش از ارسال پرداخت شود.";
    private const string ShippingPaymentBlocked =
        "⚠️ ثبت آدرس جهت ارسال متوقف شد. ابتدا وضعیت فاکتورهای در انتظار پرداخت را در حسابداری تعیین کنید.";
    private const string ShippingPaymentApprovalPrefix = "shipping:approveunpaid:";

    private IQueryable<Invoice> UnpaidShippingInvoices(Guid customerId) =>
        _db.Invoices.AsNoTracking().Where(invoice=>!invoice.IsDeleted&&invoice.Status==InvoiceStatus.Issued&&
            invoice.Order!=null&&!invoice.Order.IsDeleted&&invoice.Order.Status!=OrderStatus.Cancelled&&
            invoice.Order.CustomerId==customerId&&invoice.Order.Customer!=null&&!invoice.Order.Customer.IsDeleted);

    private async Task<bool> RequireShippingPaymentApprovalAsync(TelegramShippingPreparationDraft draft,
        TelegramUser? actor, CancellationToken ct, TelegramCallbackQuery? callback = null, TelegramMessage? addressInput = null)
    {
        var unpaid = await UnpaidShippingInvoices(draft.CustomerId).Select(i => i.Id).ToArrayAsync(ct);
        if (unpaid.Length == 0) return true;
        var canApprove = actor is { IsBot: not true } && actor.Id == draft.UserId &&
            IsAuthorizedShippingOperator(actor.Id) && await IsAuthorizedInvoiceActionAdminAsync(actor.Id, ct);
        if (canApprove && unpaid.All(draft.ApprovedUnpaidInvoices.Contains)) return true;

        if (callback is not null)
            await _sender.AnswerCallbackAsync(callback.Id, ShippingPaymentBlocked, ct, true);
        if (!canApprove)
        {
            if (callback is null)
                await ReplyAsync(draft.ChatId, ShippingPaymentBlocked, ct);
            return false;
        }

        var approval = new ShippingPaymentApproval(Guid.NewGuid(), draft.CustomerId, draft.AddressId,
            draft.Stage, unpaid, DateTime.UtcNow.AddMinutes(5), addressInput);
        Interlocked.Exchange(ref draft.PaymentApproval, approval);
        await _sender.SendInlineKeyboardAsync(draft.ChatId.ToString(),
            (callback is null ? ShippingPaymentBlocked + "\n\n" : "") +
            "ادامه با وجود فاکتور در انتظار پرداخت فقط با تأیید مجدد ادمین انجام می‌شود.",
            [[new TelegramInlineButton("تأیید ثبت با وجود فاکتور در انتظار پرداخت", ShippingPaymentApprovalPrefix + approval.Token.ToString("N"))]], ct);
        return false;
    }

    private async Task ConfirmShippingPaymentOverrideAsync(TelegramCallbackQuery callback, Guid token, CancellationToken ct)
    {
        if (callback.From.IsBot == true || !IsAuthorizedShippingOperator(callback.From.Id) ||
            !await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id, ct) ||
            !_orderFlowDrafts.TryGetShippingPreparation(callback.Message!.Chat.Id, callback.From.Id, out var draft) ||
            draft.PaymentApproval is not { } approval || approval.Token != token || approval.ExpiresAt <= DateTime.UtcNow ||
            approval.CustomerId != draft.CustomerId || approval.AddressId != draft.AddressId || approval.Stage != draft.Stage ||
            !ReferenceEquals(Interlocked.CompareExchange(ref draft.PaymentApproval, null, approval), approval))
        {
            await _sender.AnswerCallbackAsync(callback.Id, "این تأیید معتبر نیست یا منقضی شده است.", ct, true);
            return;
        }
        draft.ApprovedUnpaidInvoices = approval.InvoiceIds;
        if (approval.AddressInput is not null)
        {
            await _sender.AnswerCallbackAsync(callback.Id, "تأیید ادمین ثبت شد.", ct);
            await TryHandleShippingPreparationMessageAsync(approval.AddressInput, ct);
        }
        else
            await DispatchPreparedShippingRequestAsync(callback, ct);
    }

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
            if(customerId.HasValue&&await UnpaidShippingInvoices(customerId.Value).AnyAsync(ct))
            {
                await _sender.AnswerCallbackAsync(callback.Id,ShippingUnpaidInvoiceWarning,ct,true);
                return;
            }
        }
        await _sender.AnswerCallbackAsync(callback.Id,success,ct,success is not null);
    }
}
