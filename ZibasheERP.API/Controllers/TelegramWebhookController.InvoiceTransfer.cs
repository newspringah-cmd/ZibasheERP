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
    private sealed record InvoiceTransferDraft(Guid Operation,string Step,DateTime Until,InvoiceTransferService.Selection? Selection=null,InvoiceTransferService.Preview? Preview=null,string? Token=null,InvoiceTransferService.Items? Choices=null,Guid[]? Selected=null,int Page=0);
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
                await TransferPrompt(chat,"🔁 انتقال فاکتور\nکپشن کامل فاکتور را کپی یا فوروارد کنید؛ کپشن عکس/PDF هم پذیرفته می‌شود.\nپس از دریافت کپشن، یک یا چند آیتم را انتخاب کنید. وضعیت پرداخت و مبلغ آیتم‌ها حفظ می‌شوند؛ در انتقال جزئی، فاکتور جدا برای گیرنده صادر می‌شود. هنوز انتقالی انجام نشده است.",ct);return;
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
            if(action.StartsWith("pick:",StringComparison.Ordinal))
            {
                if(!InvoiceTransferDrafts.TryGetValue(key,out var picking)||picking.Until<DateTime.UtcNow||picking.Step!="items"||picking.Choices is null)
                    throw new InvalidOperationException("انتخاب آیتم منقضی شد؛ دوباره کپشن را بفرستید.");
                var parts=action.Split(':');
                if(parts.Length!=3||parts[1]!=picking.Token)throw new InvalidOperationException("این دکمه مربوط به انتخاب قبلی است.");
                var selected=(picking.Selected??[]).ToHashSet();
                if(parts[2]=="all")selected=picking.Choices.Lines.Where(x=>x.Blocked is null).Select(x=>x.Id).ToHashSet();
                else if(parts[2]=="done")
                {
                    var selection=await new InvoiceTransferService(_db).SelectAsync(picking.Choices,selected.ToArray(),ct);
                    InvoiceTransferDrafts[key]=picking with{Step="recipient",Selection=selection};
                    await TransferPrompt(chat,$"{selected.Count} آیتم انتخاب شد.\nآیدی انتقال‌گیرنده را وارد کنید: @username یا شناسهٔ عددی تلگرام.\nگروه حسابداری گیرنده باید متصل باشد.",ct);return;
                }
                else if(parts[2].StartsWith('p')&&int.TryParse(parts[2][1..],out var page))
                    picking=picking with{Page=Math.Clamp(page,0,(picking.Choices.Lines.Length-1)/10)};
                else if(int.TryParse(parts[2],out var index)&&index>=0&&index<picking.Choices.Lines.Length)
                {
                    var item=picking.Choices.Lines[index];
                    if(item.Blocked is not null)throw new InvalidOperationException(item.Name+": "+item.Blocked+"؛ قابل انتقال نیست.");
                    if(!selected.Add(item.Id))selected.Remove(item.Id);
                }
                else throw new InvalidOperationException("انتخاب نامعتبر است.");
                picking=picking with{Selected=selected.ToArray()};InvoiceTransferDrafts[key]=picking;
                await ShowTransferItems(chat,picking,ct);return;
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
                var choices=await new InvoiceTransferService(_db).FindItemsAsync(text,ct);
                draft=draft with{Step="items",Choices=choices,Selected=[],Token=Guid.NewGuid().ToString("N")[..12],Page=0};
                InvoiceTransferDrafts[key]=draft;await ShowTransferItems(chat,draft,ct);return true;
            }
            if(draft.Step=="recipient")
            {
                var preview=await new InvoiceTransferService(_db).PreviewAsync(draft.Selection!,text,ct);var token=Guid.NewGuid().ToString("N")[..12];
                InvoiceTransferDrafts[key]=draft with{Step="confirm",Preview=preview,Token=token};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"🔁 پیش‌نمایش انتقال فاکتور {preview.Source.Number}\nاز: {preview.From}\nبه: {preview.To}\n{string.Join("\n",preview.Lines)}\n\nمبلغ کل: {preview.Amount:N0} تومان\nپرداخت‌شده: {preview.Paid:N0} تومان\nماندهٔ بدهی برای انتقال: {preview.Outstanding:N0} تومان\nوضعیت: {preview.Status}\n\nمبلغ و تخفیف آیتم‌های انتخاب‌شده حفظ می‌شوند؛ وجهی جابه‌جا نمی‌شود. پرداخت جزئی به نسبت مبلغ آیتم‌ها تقسیم می‌شود. در انتقال جزئی، آیتم‌های باقی‌مانده در فاکتور مبدا می‌مانند و نسخهٔ اصلاح‌شده برای صاحب قبلی و فاکتور جدا برای گیرنده ارسال می‌شود؛ نسخهٔ قدیمی پس از تحویل جایگزین حذف می‌شود. انتقال تأیید شود؟",
                    [[new("✅ تأیید انتقال فاکتور",InvoiceTransferPrefix+"confirm:"+token)],[new("❌ انصراف",InvoiceTransferPrefix+"cancel")]],ct);return true;
            }
            await TransferPrompt(chat,"از دکمه‌های همین پیش‌نمایش استفاده کنید.",ct);return true;
        }
        catch(InvalidOperationException e){await TransferPrompt(chat,e.Message,ct);return true;}
    }
    private Task ShowTransferItems(long chat,InvoiceTransferDraft draft,CancellationToken ct)
    {
        var choices=draft.Choices!;var selected=(draft.Selected??[]).ToHashSet();
        var rows=new List<IReadOnlyCollection<TelegramInlineButton>>();
        var prefix=InvoiceTransferPrefix+"pick:"+draft.Token+":";
        foreach(var (line,index) in choices.Lines.Select((x,i)=>(x,i)).Skip(draft.Page*10).Take(10))
        {
            var name=line.Name.Length>55?line.Name[..55]+"…":line.Name;
            rows.Add([new($"{(line.Blocked is not null?"⛔":selected.Contains(line.Id)?"✅":"⬜")} {index+1}. {name} — {line.Volume:0.##} میل"+(line.Blocked is null?"":" — "+line.Blocked),prefix+index)]);
        }
        var pages=new List<TelegramInlineButton>();
        if(draft.Page>0)pages.Add(new("⬅️ قبلی",prefix+"p"+(draft.Page-1)));
        if((draft.Page+1)*10<choices.Lines.Length)pages.Add(new("بعدی ➡️",prefix+"p"+(draft.Page+1)));
        if(pages.Count>0)rows.Add(pages);
        rows.Add([new("همهٔ موارد قابل انتقال",prefix+"all")]);
        rows.Add([new($"ادامه با {selected.Count} آیتم ✅",prefix+"done")]);rows.Add([new("❌ انصراف",InvoiceTransferPrefix+"cancel")]);
        return _sender.SendInlineKeyboardAsync(chat.ToString(),$"فاکتور {choices.Source.Number}\nآیتم‌های موردنظر برای انتقال را انتخاب کنید. فقط موارد انتخاب‌شده منتقل می‌شوند؛ اقلام ارسال‌شده و داخل بستهٔ آماده‌سازی قابل انتخاب نیستند.\nصفحهٔ {draft.Page+1} از {(choices.Lines.Length+9)/10}",rows,ct);
    }

}
