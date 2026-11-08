using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string InvoiceTransferPrefix="invoiceadmin:transfer:";
    private sealed record InvoiceTransferDraft(Guid Operation,string Step,DateTime Until,InvoiceTransferService.Selection? Selection=null,InvoiceTransferService.Preview? Preview=null,string? Token=null);
    private static readonly ConcurrentDictionary<(long Chat,long User),InvoiceTransferDraft> InvoiceTransferDrafts=new();
    private Task TransferPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new("❌ انصراف",InvoiceTransferPrefix+"cancel")]],ct);
    private async Task HandleInvoiceTransferCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var user=callback.From.Id;var key=(chat,user);
        if(callback.Message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(user,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"انتقال فاکتور فقط برای ادمین‌های حسابداری در خصوصی ربات است.",ct,true);return;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);var action=callback.Data![InvoiceTransferPrefix.Length..];
        try
        {
            if(action=="start")
            {
                ClearAdminWorkflowDrafts(chat,user);InvoiceTransferDrafts[key]=new(Guid.NewGuid(),"caption",DateTime.UtcNow.AddMinutes(30));
                await TransferPrompt(chat,"🔁 انتقال فاکتور\nکپشن کامل فاکتور را کپی یا فوروارد کنید؛ کپشن عکس/PDF هم پذیرفته می‌شود.\nوضعیت پرداخت، مبلغ و شمارهٔ فاکتور حفظ می‌شوند. هنوز انتقالی انجام نشده است.",ct);return;
            }
            if(action=="cancel")
            {InvoiceTransferDrafts.TryRemove(key,out _);await _sender.SendInlineKeyboardAsync(chat.ToString(),"انتقال لغو شد؛ تغییری در مالکیت ایجاد نشد.",[[new("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;}
            if((action.StartsWith("status:")||action.StartsWith("retry:"))&&Guid.TryParseExact(action[(action.IndexOf(':')+1)..],"N",out var operation))
            {
                var receipt=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==operation&&n.Channel==InvoiceTransferService.ReceiptChannel&&!n.IsDeleted,ct)
                    ??throw new InvalidOperationException("انتقال پیدا نشد.");
                var saved=JsonSerializer.Deserialize<InvoiceTransferService.Result>(receipt.Payload)!;
                var work=await _db.NotificationOutbox.SingleAsync(n=>n.Channel==InvoiceTransferService.WorkChannel&&n.OrderId==saved.OrderId&&n.Payload.Contains(operation.ToString()),ct);
                var progress=JsonSerializer.Deserialize<InvoiceTransferService.Progress>(work.Payload)!;
                if(action.StartsWith("retry:")&&work.Status==NotificationOutboxStatus.Failed)
                {progress=progress with{Error=null};work.Status=NotificationOutboxStatus.Pending;work.NextAttemptAt=DateTime.UtcNow;work.LockedUntil=null;work.LastError=null;work.Payload=JsonSerializer.Serialize(progress);await _db.SaveChangesAsync(ct);}
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"🔁 فاکتور {saved.Number}\nاز: {saved.From}\nبه: {saved.To}\nمالکیت و ماندهٔ بدهی منتقل شده‌اند.\nگزارش واریز: {(progress.ReportUpdated?"به‌روز شد":progress.ReportMissing?"نیازمند بررسی دستی":"در انتظار به‌روزرسانی")}\nحذف پیام‌های مبدا: {progress.Deleted} از {saved.Cleanup.Length}\nوضعیت: {(progress.Complete?"✅ تکمیل شد":work.Status==NotificationOutboxStatus.Failed?"⚠️ نیازمند بررسی":"⏳ در حال تکمیل")}\n{progress.Error??work.LastError}",
                    [[new("🔄 بررسی وضعیت",InvoiceTransferPrefix+"status:"+operation.ToString("N"))],[new("🔄 تلاش مجدد پاک‌سازی",InvoiceTransferPrefix+"retry:"+operation.ToString("N"))],[new("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;
            }
            if(!InvoiceTransferDrafts.TryGetValue(key,out var draft)||draft.Until<DateTime.UtcNow||draft.Step!="confirm"||draft.Preview is null||action!="confirm:"+draft.Token)
                throw new InvalidOperationException("پیش‌نمایش منقضی یا مرحله تغییر کرده؛ دوباره از منوی فاکتورها شروع کنید.");
            var result=await new InvoiceTransferService(_db).ExecuteAsync(draft.Operation,draft.Preview,user,ct);
            InvoiceTransferDrafts.TryRemove(key,out _);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"✅ مالکیت فاکتور {result.Number} منتقل شد.\nاز: {result.From}\nبه: {result.To}\nماندهٔ بدهی منتقل‌شده: {result.DebtMoved:N0} تومان\nپرداخت قبلی: {result.Paid:N0} تومان؛ حفظ شد.\nارسال نسخهٔ جدید، اصلاح گزارش واریز و پاک‌سازی پیام‌های ثبت‌شدهٔ مبدا از صف پیگیری می‌شود.",
                [[new("🔄 وضعیت ارسال و پاک‌سازی",InvoiceTransferPrefix+"status:"+draft.Operation.ToString("N"))],[new("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);
        }
        catch(InvalidOperationException e){await TransferPrompt(chat,e.Message,ct);}
        catch(DbUpdateConcurrencyException){await TransferPrompt(chat,"اطلاعات هم‌زمان عوض شد؛ انتقال ثبت نشد. پیش‌نمایش تازه بگیرید.",ct);}
    }
    private async Task<bool> TryHandleInvoiceTransferMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!InvoiceTransferDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var draft))return false;
        var key=(message.Chat.Id,message.From.Id);var chat=message.Chat.Id;
        if(message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct)){InvoiceTransferDrafts.TryRemove(key,out _);return false;}
        var text=(message.Text??message.Caption??"").Trim();
        if(text.StartsWith('/')){InvoiceTransferDrafts.TryRemove(key,out _);return false;}
        if(draft.Until<DateTime.UtcNow){InvoiceTransferDrafts.TryRemove(key,out _);await TransferPrompt(chat,"فرایند منقضی شد؛ دوباره شروع کنید.",ct);return true;}
        try
        {
            if(draft.Step=="caption")
            {
                var selection=await new InvoiceTransferService(_db).FindAsync(text,ct);InvoiceTransferDrafts[key]=draft with{Step="recipient",Selection=selection};
                await TransferPrompt(chat,$"فاکتور {selection.Number} پیدا شد.\nآیدی انتقال‌گیرنده را وارد کنید: @username یا شناسهٔ عددی تلگرام.\nگروه حسابداری گیرنده باید متصل باشد.",ct);return true;
            }
            if(draft.Step=="recipient")
            {
                var preview=await new InvoiceTransferService(_db).PreviewAsync(draft.Selection!,text,ct);var token=Guid.NewGuid().ToString("N")[..12];
                InvoiceTransferDrafts[key]=draft with{Step="confirm",Preview=preview,Token=token};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"🔁 پیش‌نمایش انتقال فاکتور {preview.Source.Number}\nاز: {preview.From}\nبه: {preview.To}\n{string.Join("\n",preview.Lines)}\n\nمبلغ کل: {preview.Amount:N0} تومان\nپرداخت‌شده: {preview.Paid:N0} تومان\nماندهٔ بدهی برای انتقال: {preview.Outstanding:N0} تومان\nوضعیت: {preview.Status}\n\nمبلغ، تخفیف و سابقهٔ پرداخت حفظ می‌شوند؛ وجهی جابه‌جا نمی‌شود. فاکتور به گروه گیرنده ارسال و پس از تحویل موفق، پیام‌های ثبت‌شدهٔ مبدا حذف می‌شوند. انتقال تأیید شود؟",
                    [[new("✅ تأیید انتقال فاکتور",InvoiceTransferPrefix+"confirm:"+token)],[new("❌ انصراف",InvoiceTransferPrefix+"cancel")]],ct);return true;
            }
            await TransferPrompt(chat,"از دکمه‌های همین پیش‌نمایش استفاده کنید.",ct);return true;
        }
        catch(InvalidOperationException e){await TransferPrompt(chat,e.Message,ct);return true;}
    }
}
