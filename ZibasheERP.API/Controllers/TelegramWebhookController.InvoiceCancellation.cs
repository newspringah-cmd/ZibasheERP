using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.API.Telegram;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    internal async Task ProcessInvoiceRestockTrackingAsync(NotificationOutbox job,CancellationToken ct)
    {
        if(job.Channel!=InvoiceRestockTracking.Channel||!Guid.TryParse(job.Recipient,out var id))throw new InvalidOperationException("Invalid restock report job");
        var invoice=await _db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Customer)
            .Include(i=>i.Order).ThenInclude(o=>o!.Items).SingleOrDefaultAsync(i=>i.Id==id&&!i.IsDeleted&&i.OrderId==job.OrderId,ct);
        if(invoice?.Order is null||invoice.Status!=InvoiceStatus.Cancelled||invoice.Order.Status!=OrderStatus.Cancelled)
            throw new InvalidOperationException("Restock report invoice mismatch");
        var published=(await InvoiceRestockTracking.PublishedInvoiceIdsAsync(_db,[invoice.OrderId],ct)).Contains(id);
        async Task Edit(string? chat,long? message,string text,IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons)
        {
            if(string.IsNullOrWhiteSpace(chat)||message is not >0)return; // A future first report uses the same persisted status.
            if(chat.Trim()!=_options.NewPaymentsChatId.Trim())throw new InvalidOperationException("Restock report destination mismatch");
            var result=await _sender.EditTextWithKeyboardAsync(chat,message.Value,text,buttons,ct);
            if(!result.IsSuccessful&&!IsTelegramMessageUnchanged(result.Error))throw new InvalidOperationException("Restock report edit retry required");
        }
        if(invoice.Order.InvoiceIssuanceBatchId is Guid batch)
        {
            var lists=invoice.Order.Items.Select(x=>x.SalesListId).ToHashSet();
            foreach(var report in await _invoiceIssuanceService.GetPaymentTrackingReportsAsync(batch,ct))
                if(lists.Contains(report.SalesListId))await Edit(report.TelegramChatId,report.TelegramMessageId,report.Message,BuildPaymentTrackingButtons(report));
        }
        else await Edit(invoice.TelegramPaymentTrackingChatId,invoice.TelegramPaymentTrackingMessageId,
            FormatManualPaymentTrackingMessage(invoice,published),BuildManualPaymentTrackingButtons(invoice));
        // First show every cancellation in blue. Keep a restock job retryable
        // until publication succeeds so the unknown note becomes a verified transfer.
        if(!published&&await _db.NotificationOutbox.AsNoTracking().AnyAsync(n=>!n.IsDeleted&&n.OrderId==invoice.OrderId&&
            n.Channel=="InvoiceCancellationReceipt"&&n.EventType=="InvoiceCancelledAndRestocked"&&n.Status==NotificationOutboxStatus.Processed,ct))
            throw new InvalidOperationException("RestockPublicationPending");
    }
    internal async Task ProcessInvoiceCancellationCleanupAsync(NotificationOutbox job,CancellationToken ct)
    {
        var target=JsonSerializer.Deserialize<InvoiceMessageLifecycle.Cleanup>(job.Payload)??throw new InvalidOperationException("Invalid invoice cleanup target");
        var invoice=await _db.Invoices.AsNoTracking().SingleOrDefaultAsync(i=>i.Id==target.InvoiceId&&i.OrderId==job.OrderId&&i.Status==InvoiceStatus.Cancelled,ct);
        if(job.Channel!="InvoiceCancellationCleanup"||job.Recipient!=target.ChatId||target.MessageId<=0||invoice is null)
            throw new InvalidOperationException("Invoice cleanup is not authorized for this message");
        NotificationOutbox? row=null;InvoiceMessageLifecycle.Receipt? receipt=null;
        if(target.ReceiptId is Guid receiptId)
        {
            row=await _db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==receiptId&&n.Channel=="InvoiceMessageReceipt"&&n.OrderId==job.OrderId,ct);
            receipt=row is null?null:JsonSerializer.Deserialize<InvoiceMessageLifecycle.Receipt>(row.Payload);
            if(receipt is null||receipt.InvoiceId!=target.InvoiceId||receipt.ChatId!=target.ChatId||receipt.MessageId!=target.MessageId)throw new InvalidOperationException("Invoice message receipt mismatch");
            if(receipt.DeletedAtUtc!=null)return;
        }
        else if(invoice.TelegramInvoiceChatId!=target.ChatId||invoice.TelegramInvoiceMessageId!=target.MessageId)
            throw new InvalidOperationException("Invoice PDF receipt mismatch");
        var result=await _sender.DeleteMessageAsync(target.ChatId,target.MessageId,ct);
        if(!result.IsSuccessful&&result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
        {
            if(result.Error?.Contains("can't be deleted",StringComparison.OrdinalIgnoreCase)==true||result.Error?.Contains("not enough rights",StringComparison.OrdinalIgnoreCase)==true)
                throw new InvalidOperationException("InvoiceMessageDeletionRestricted");
            throw new InvalidOperationException("Invoice message cleanup retry required");
        }
        if(row is not null&&receipt is not null)
        {
            row.Payload=JsonSerializer.Serialize(receipt with{DeletedAtUtc=DateTime.UtcNow});row.UpdatedAt=DateTime.UtcNow;await _db.SaveChangesAsync(ct);
        }
    }
    private const string InvoiceCancelPrefix="invoiceadmin:cancelinvoice:";
    private const string InvoiceCancelStatusPrefix="invoiceadmin:icstatus:";
    private sealed record InvoiceCancelDraft(Guid Operation,bool Returned,string Step,DateTime Until,
        InvoiceCancellationService.Preview? Preview=null,int Index=0,Dictionary<Guid,decimal>? Prices=null,string? Token=null);
    private static readonly ConcurrentDictionary<(long Chat,long User),InvoiceCancelDraft> InvoiceCancelDrafts=new();
    private Task InvoiceCancelPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new TelegramInlineButton("❌ انصراف",InvoiceCancelPrefix+"cancel")]],ct);
    private async Task HandleInvoiceCancellationCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var key=(chat,callback.From.Id);
        if(callback.Message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"این عملیات فقط برای ادمین‌های حسابداری در خصوصی ربات است.",ct,true);return;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        if(callback.Data!.StartsWith(InvoiceCancelStatusPrefix,StringComparison.Ordinal))
        {
            if(!Guid.TryParseExact(callback.Data[InvoiceCancelStatusPrefix.Length..],"N",out var operation))return;
            var receipt=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==operation&&n.Channel=="InvoiceCancellationReceipt",ct);
            if(receipt is null){await InvoiceCancelPrompt(chat,"عملیات پیدا نشد.",ct);return;}
            var result=JsonSerializer.Deserialize<InvoiceCancellationService.Result>(receipt.Payload)!;
            var cleanup=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==receipt.OrderId&&n.Channel=="InvoiceCancellationCleanup").Select(n=>n.Status).ToArrayAsync(ct);
            var publications=await _db.NotificationOutbox.AsNoTracking().Where(n=>result.Offers.Contains(n.Id)).Select(n=>n.Status).ToArrayAsync(ct);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"✅ لغو مالی انجام شده است.\nپاکسازی پیام‌های ثبت‌شده: {cleanup.Count(s=>s==NotificationOutboxStatus.Processed)} از {cleanup.Length}\nانتشار موجودی: {publications.Count(s=>s==NotificationOutboxStatus.Processed)} از {result.Offers.Length}\nنیازمند بررسی: {cleanup.Count(s=>s==NotificationOutboxStatus.Failed)+publications.Count(s=>s==NotificationOutboxStatus.Failed)}\n\nبرای پیام‌های قدیمیِ بدون شناسهٔ ثبت‌شده، روی خود پیام در گروه ریپلای کنید و بنویسید:\n/invoicecleanup شماره‌فاکتور",
                [[new TelegramInlineButton("🔄 بررسی وضعیت",callback.Data)],[new TelegramInlineButton("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;
        }
        var action=callback.Data[InvoiceCancelPrefix.Length..];
        if(action is "start" or "return-start")
        {
            ClearAdminWorkflowDrafts(chat,callback.From.Id);var returned=action=="return-start";
            InvoiceCancelDrafts[key]=new(Guid.NewGuid(),returned,"caption",DateTime.UtcNow.AddMinutes(20));
            await InvoiceCancelPrompt(chat,(returned?"📦 حذف فاکتور و ارسال مستقیم به موجودی":"🗑 حذف فاکتور")+"\nکپشن کامل فاکتور را کپی و ارسال کنید؛ کپشن عکس یا PDF هم پذیرفته می‌شود.\nابتدا فاکتور پیدا و پیش‌نمایش داده می‌شود؛ هنوز هیچ تغییری انجام نمی‌شود.",ct);return;
        }
        if(action=="cancel"){InvoiceCancelDrafts.TryRemove(key,out _);await _sender.SendInlineKeyboardAsync(chat.ToString(),"عملیات لغو شد؛ فاکتور تغییری نکرد.",[[new TelegramInlineButton("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;}
        try
        {
            if(!InvoiceCancelDrafts.TryGetValue(key,out var d)||d.Until<DateTime.UtcNow||d.Preview is null)throw new InvalidOperationException("فرایند منقضی شده؛ از منوی فاکتورها دوباره شروع کنید.");
            if(action=="edit-price:"+d.Index&&d.Step=="compare")
            {InvoiceCancelDrafts[key]=d with{Step="price"};await InvoiceCancelPrompt(chat,$"قیمت هر میلِ موجودی «{d.Preview.Lines[d.Index].Name}» را به تومان وارد کنید؛ مبلغ کل یا قیمت شیشه را وارد نکنید.",ct);return;}
            if(action=="accept-price:"+d.Index&&d.Step=="compare")
            {
                d=d with{Index=d.Index+1};InvoiceCancelDrafts[key]=d;
                if(d.Index<d.Preview.Lines.Length)await ShowInvoiceReturnPriceAsync(chat,callback.From.Id,d,ct);
                else await ShowInvoiceCancellationConfirmationAsync(chat,callback.From.Id,d,ct);
                return;
            }
            if(action.StartsWith("confirm:",StringComparison.Ordinal)&&d.Step=="confirm"&&action[8..]==d.Token)
            {
                var result=await new InvoiceCancellationService(_db).ExecuteAsync(d.Operation,d.Preview,d.Returned,d.Prices??new Dictionary<Guid,decimal>(),callback.From.Id,_options.ReadyInventoryChannelId,ct);
                InvoiceCancelDrafts.TryRemove(key,out _);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"✅ فاکتور {d.Preview.Number} برای {d.Preview.Customer} لغو شد.\nمبلغ اصلاح‌شدهٔ بدهی: {result.ReversedAmount:N0} تومان\n"+
                    (result.Returned?$"{result.Codes.Length} بستهٔ موجودی ثبت شد؛ کدها: {string.Join("، ",result.Codes)}\nانتشار با عکس در کانال موجودی در حال انجام است.":"موجودی جدیدی ایجاد نشد.")+"\nپاکسازی استیکر، عکس، متن و PDF ثبت‌شدهٔ همین فاکتور از گروه در حال انجام است؛ نتیجه را با دکمهٔ زیر بررسی کنید.",
                    [[new TelegramInlineButton("🔄 وضعیت پاکسازی و انتشار",InvoiceCancelStatusPrefix+d.Operation.ToString("N"))],[new TelegramInlineButton("↩️ فاکتورها","invoiceadmin:menu:invoices")]],ct);return;
            }
            throw new InvalidOperationException("این دکمه مربوط به پیش‌نمایش فعلی نیست.");
        }
        catch(InvalidOperationException ex){await InvoiceCancelPrompt(chat,ex.Message,ct);}
    }
    private async Task<bool> TryHandleInvoiceCleanupReplyAsync(TelegramMessage message,CancellationToken ct)
    {
        var text=message.Text?.Trim();if(text?.StartsWith("/invoicecleanup",StringComparison.OrdinalIgnoreCase)!=true)return false;
        if(message.From is null||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct))
        {await ReplyAsync(message.Chat.Id,"پاکسازی فاکتور فقط برای مدیر و حسابدار مجاز است.",ct);return true;}
        var replied=message.ReplyToMessage;
        var parts=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(!IsGroup(message.Chat.Type)||parts.Length!=2||replied is not {MessageId:>0,From.IsBot:true}||
            !long.TryParse(_options.BotToken.Split(':')[0],out var botId)||replied.From.Id!=botId)
        {await ReplyAsync(message.Chat.Id,"روی استیکر سلام، عکس یا پیام خود زیبا در همان گروه ریپلای کنید و دستور را با شمارهٔ فاکتور لغوشده بفرستید:\n/invoicecleanup INV-261102-4143",ct);return true;}
        var invoice=await _db.Invoices.Include(i=>i.Order).SingleOrDefaultAsync(i=>i.InvoiceNumber==parts[1]&&!i.IsDeleted&&i.Status==InvoiceStatus.Cancelled,ct);
        var chat=message.Chat.Id.ToString(CultureInfo.InvariantCulture);
        if(invoice?.Order is null||!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==chat&&g.CustomerId==invoice.Order.CustomerId,ct))
        {await ReplyAsync(message.Chat.Id,"فاکتور لغوشده با مشتری این گروه تطبیق ندارد؛ پیامی حذف نشد.",ct);return true;}
        var kind=replied.Sticker!=null?"InvoiceGreeting":replied.Photo is {Count:>0}?"InvoicePerfumePhoto":
            replied.Text?.StartsWith("سلام",StringComparison.Ordinal)==true?"InvoiceGreeting":"InvoiceIssued";
        var date=replied.Date.HasValue?DateTimeOffset.FromUnixTimeSeconds(replied.Date.Value).UtcDateTime:(DateTime?)null;
        if(date is null){await ReplyAsync(message.Chat.Id,"تاریخ پیام اصلی قابل تأیید نیست؛ پیامی حذف نشد.",ct);return true;}
        var from=date.Value.AddMinutes(-3);var until=date.Value.AddMinutes(3);
        var source=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==invoice.OrderId&&n.Channel=="Telegram"&&n.EventType==kind&&n.Status==NotificationOutboxStatus.Processed&&n.ProcessedAt>=from&&n.ProcessedAt<=until).Select(n=>(Guid?)n.Id).FirstOrDefaultAsync(ct);
        if(source is null||(kind=="InvoiceIssued"&&replied.Text?.Contains(invoice.InvoiceNumber,StringComparison.Ordinal)!=true))
        {await ReplyAsync(message.Chat.Id,"نوع یا زمان پیام با پیام‌های این فاکتور تطبیق ندارد؛ پیامی حذف نشد.",ct);return true;}
        await InvoiceMessageLifecycle.RecordAsync(_db,invoice.OrderId,source.Value,chat,replied.MessageId,kind,ct);await _db.SaveChangesAsync(ct);
        await ReplyAsync(message.Chat.Id,$"پیام انتخاب‌شدهٔ فاکتور {invoice.InvoiceNumber} برای پاکسازی ثبت شد.",ct);return true;
    }
    private async Task<bool> TryHandleInvoiceCancellationMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!InvoiceCancelDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var d))return false;
        var key=(message.Chat.Id,message.From.Id);var text=(message.Text??message.Caption??"").Trim();
        if(message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct))
        {InvoiceCancelDrafts.TryRemove(key,out _);return false;}
        if(text.StartsWith('/')||d.Until<DateTime.UtcNow){InvoiceCancelDrafts.TryRemove(key,out _);return false;}
        try
        {
            if(d.Step=="caption")
            {
                var preview=await new InvoiceCancellationService(_db).ResolveAsync(text,d.Returned,ct);
                d=d with{Preview=preview,Prices=preview.Lines.ToDictionary(l=>l.ItemId,l=>l.CurrentPrice),Index=0};
                InvoiceCancelDrafts[key]=d;
                if(d.Returned)await ShowInvoiceReturnPriceAsync(message.Chat.Id,message.From.Id,d,ct);
                else await ShowInvoiceCancellationConfirmationAsync(message.Chat.Id,message.From.Id,d,ct);
                return true;
            }
            if(d.Step=="price")
            {
                var numeric=NormalizeNumber(text).Replace("٬","").Replace(",","").Replace('٫','.');
                if(!decimal.TryParse(numeric,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var price)||price<=0||price>1000000000000m||decimal.Round(price,2)!=price)throw new InvalidOperationException("قیمت مثبت هر میل را به تومان وارد کنید؛ حداکثر دو رقم اعشار.");
                var prices=new Dictionary<Guid,decimal>(d.Prices!){[d.Preview!.Lines[d.Index].ItemId]=price};d=d with{Prices=prices};
                await ShowInvoiceReturnPriceAsync(message.Chat.Id,message.From.Id,d,ct);return true;
            }
            await InvoiceCancelPrompt(message.Chat.Id,"از دکمه‌های پیش‌نمایش فعلی استفاده کنید.",ct);return true;
        }
        catch(InvalidOperationException ex){await InvoiceCancelPrompt(message.Chat.Id,ex.Message,ct);return true;}
    }
    private async Task ShowInvoiceReturnPriceAsync(long chat,long user,InvoiceCancelDraft d,CancellationToken ct)
    {
        var p=d.Preview!;var line=p.Lines[d.Index];var price=d.Prices![line.ItemId];
        InvoiceCancelDrafts[(chat,user)]=d with{Step="compare",Token=null};
        await _sender.SendInlineKeyboardAsync(chat.ToString(),$"📦 انتقال فاکتور {p.Number} به موجودی\nمشتری: {p.Customer}\nآیتم {d.Index+1} از {p.Lines.Length}: {line.Name}\nحجم: {line.Volume:0.#} میل\n\nقیمت هر میل در فاکتور: {line.InvoicePrice:N0} تومان\nقیمت روز هر میل: {line.CurrentPrice:N0} تومان\nاختلاف با فاکتور: {line.CurrentPrice-line.InvoicePrice:+#,0;-#,0;0} تومان\n\nقیمت انتخابی موجودی، هر میل: {price:N0} تومان\nشیشه: {line.BottleName} — {line.BottlePrice:N0} تومان\nمبلغ نهایی موجودی: {price*line.Volume+line.BottlePrice:N0} تومان\nجایگاه داخلی: {line.Position}\n\nقیمت شیشه جداست؛ در صورت نیاز قیمت هر میل را اصلاح کنید.",
            [[new TelegramInlineButton("✅ همین قیمت؛ ادامه",InvoiceCancelPrefix+"accept-price:"+d.Index)],[new TelegramInlineButton("✏️ اصلاح قیمت هر میل",InvoiceCancelPrefix+"edit-price:"+d.Index)],[new TelegramInlineButton("❌ انصراف",InvoiceCancelPrefix+"cancel")]],ct);
    }
    private async Task ShowInvoiceCancellationConfirmationAsync(long chat,long user,InvoiceCancelDraft d,CancellationToken ct)
    {
        var p=d.Preview!;var token=Guid.NewGuid().ToString("N")[..12];InvoiceCancelDrafts[(chat,user)]=d with{Step="confirm",Token=token};
        var text=$"👁 پیش‌نمایش {(d.Returned?"لغو و انتقال به موجودی":"لغو فاکتور")}\nشماره: {p.Number}\nمشتری: {p.Customer}\nمبلغ قابل برگشت از بدهی: {p.Amount:N0} تومان\n\n"+
            string.Join("\n\n",p.Lines.Select(l=>$"🧴 {l.Name} — {l.Volume:0.#} میل\nشیشه: {l.BottleName}"+(d.Returned?$"\nقیمت موجودی هر میل: {d.Prices![l.ItemId]:N0} تومان\nمبلغ عطر و شیشه: {d.Prices[l.ItemId]*l.Volume+l.BottlePrice:N0} تومان":"")));
        foreach(var part in SplitTelegramMessage(text))await _sender.SendAsync(chat.ToString(),part,ct);
        await _sender.SendInlineKeyboardAsync(chat.ToString(),d.Returned?"با تأیید، کل فاکتور لغو و همهٔ آیتم‌های بالا به موجودی آمادهٔ ارسال منتقل و با عکس در کانال موجودی منتشر می‌شوند. این کار تخفیف فاکتور قبلی را خودکار به موجودی جدید منتقل نمی‌کند.":"با تأیید، کل فاکتور لغو و مبلغ آن از بدهی مشتری کسر می‌شود؛ موجودی جدیدی ساخته نمی‌شود.",
            [[new TelegramInlineButton(d.Returned?"✅ تأیید لغو و ارسال به موجودی":"✅ تأیید حذف فاکتور",InvoiceCancelPrefix+"confirm:"+token)],[new TelegramInlineButton("❌ انصراف",InvoiceCancelPrefix+"cancel")]],ct);
    }
}
