using System.Collections.Concurrent;
using ZibasheERP.API.Telegram;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string CustomerFinancePrefix="invoiceadmin:customer-finance:";
    private static readonly ConcurrentDictionary<(long Chat,long User),DateTime> CustomerFinanceDrafts=new();
    private bool CanViewCustomerFinance(long user,long chat,string type)=>
        IsPrimaryOwner(user)&&user==chat&&type=="private";

    private async Task HandleCustomerFinanceCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat;
        if(!CanViewCustomerFinance(callback.From.Id,chat.Id,chat.Type))
        {await _sender.AnswerCallbackAsync(callback.Id,"این گزارش فقط برای مدیر اصلی در خصوصی ربات فعال است.",ct,true);return;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        ClearAdminWorkflowDrafts(chat.Id,callback.From.Id);
        if(callback.Data==CustomerFinancePrefix+"cancel")
        {await _sender.SendInlineKeyboardAsync(chat.Id.ToString(),"گزارش لغو شد.",[[new("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;}
        if(callback.Data!=CustomerFinancePrefix+"start")return;
        foreach(var key in CustomerFinanceDrafts.Where(d=>d.Value<DateTime.UtcNow.AddMinutes(-15)).Select(d=>d.Key))CustomerFinanceDrafts.TryRemove(key,out _);
        CustomerFinanceDrafts[(chat.Id,callback.From.Id)]=DateTime.UtcNow;
        await _sender.SendInlineKeyboardAsync(chat.Id.ToString(),"📊 گزارش مالی مشتری\nآیدی مشتری را با یا بدون @ وارد کنید؛ آیدی عددی تلگرام هم پذیرفته می‌شود.\nجمع فاکتورهای پرداخت‌شده و بدهی فاکتورهای در انتظار پرداخت از تمام سوابق محاسبه می‌شود.",
            [[new("❌ انصراف",CustomerFinancePrefix+"cancel")]],ct);
    }

    private async Task<bool> TryHandleCustomerFinanceMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!CustomerFinanceDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var started))return false;
        var key=(message.Chat.Id,message.From.Id);
        if(!CanViewCustomerFinance(message.From.Id,message.Chat.Id,message.Chat.Type))
        {CustomerFinanceDrafts.TryRemove(key,out _);return false;}
        var text=message.Text?.Trim();
        if(text is null)return true;
        if(text.StartsWith('/'))
        {CustomerFinanceDrafts.TryRemove(key,out _);if(text.Equals("/cancel",StringComparison.OrdinalIgnoreCase)){await ReplyAsync(message.Chat.Id,"گزارش لغو شد.",ct);return true;}return false;}
        if(started<DateTime.UtcNow.AddMinutes(-15))
        {CustomerFinanceDrafts.TryRemove(key,out _);await ReplyAsync(message.Chat.Id,"مهلت ورود آیدی تمام شده؛ دوباره از منوی فاکتورها شروع کنید.",ct);return true;}
        try
        {
            var report=await new CustomerInvoiceFinanceService(_db).GetAsync(text,ct);
            if(report is null){await ReplyAsync(message.Chat.Id,"مشتری با این آیدی پیدا نشد؛ آیدی را بررسی و دوباره وارد کنید.",ct);return true;}
            var output=$"📊 گزارش مالی مشتری\nمشتری: {report.Identity}\nبازه: تمام سوابق\n\n"+
                $"✅ جمع فاکتورهای پرداخت‌شده: {report.PaidTotal:N0} تومان\nتعداد: {report.PaidCount}\n\n"+
                $"⏳ جمع بدهی فاکتورهای در انتظار پرداخت: {report.Outstanding:N0} تومان\nتعداد فاکتورهای در انتظار پرداخت: {report.PendingCount}";
            if(report.PartialPayments>0)output+=$"\n\nمبلغ کل فاکتورهای باز: {report.PendingTotal:N0} تومان\nپرداخت تأییدشدهٔ این فاکتورها: {report.PartialPayments:N0} تومان";
            await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),output,
                [[new("🔎 مشتری دیگر",CustomerFinancePrefix+"start")],[new("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);
            CustomerFinanceDrafts.TryRemove(key,out _);
        }
        catch(InvalidOperationException e){await ReplyAsync(message.Chat.Id,e.Message,ct);}
        return true;
    }
}
