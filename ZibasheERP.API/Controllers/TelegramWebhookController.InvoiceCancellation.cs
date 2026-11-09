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
        else
        {
            var receipts=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.OrderId==invoice.OrderId&&n.Channel=="InvoiceCancellationReceipt"&&n.Status==NotificationOutboxStatus.Processed&&!n.IsDeleted).ToArrayAsync(ct);
            var parent=receipts.Select(n=>JsonSerializer.Deserialize<InvoiceCancellationService.Result>(n.Payload)!).FirstOrDefault(r=>r.InvoiceId==invoice.Id)?.SourceInvoiceId;
            var reportInvoice=parent.HasValue?await _db.Invoices.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Customer).Include(i=>i.Order).ThenInclude(o=>o!.Items).SingleAsync(i=>i.Id==parent&&!i.IsDeleted,ct):invoice;
            var reportPublished=parent.HasValue?(await InvoiceRestockTracking.PublishedInvoiceIdsAsync(_db,[reportInvoice.OrderId],ct)).Contains(reportInvoice.Id):published;
            await Edit(reportInvoice.TelegramPaymentTrackingChatId,reportInvoice.TelegramPaymentTrackingMessageId,
                await FormatManualPaymentTrackingWithHistoryAsync(reportInvoice,reportPublished,ct),BuildManualPaymentTrackingButtons(reportInvoice));
        }
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
        InvoiceCancellationService.Preview? Preview=null,int Index=0,Dictionary<Guid,decimal>? Prices=null,string? Token=null,
        InvoiceCancellationService.Preview? Original=null,Guid[]? Selected=null,int Page=0);
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
            var revision=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Channel==InvoiceRevisionService.Channel&&n.Recipient==operation.ToString()&&!n.IsDeleted,ct);
            var progress=revision is null?null:JsonSerializer.Deserialize<InvoiceRevisionService.Progress>(revision.Payload);
            var revisionStatus=progress is null?"":$"\nفاکتور باقی‌مانده: {(progress.Complete?"✅ نسخهٔ اصلاح‌شده ارسال شد":"⏳ در انتظار ارسال موفق یا تکمیل پاک‌سازی")}\nحذف خودکار پیام‌های قدیمی: {progress.Deleted-(progress.ManualCleanup?.Length??0)} از {progress.Cleanup.Length}\nنیازمند حذف دستی: {progress.ManualCleanup?.Length??0}\n{revision!.LastError}";
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"✅ اصلاح مالی انجام شده است.\nپاکسازی پیام‌های ثبت‌شده: {cleanup.Count(s=>s==NotificationOutboxStatus.Processed)} از {cleanup.Length}\nانتشار موجودی: {publications.Count(s=>s==NotificationOutboxStatus.Processed)} از {result.Offers.Length}\nنیازمند بررسی: {cleanup.Count(s=>s==NotificationOutboxStatus.Failed)+publications.Count(s=>s==NotificationOutboxStatus.Failed)}{revisionStatus}\n\nبرای پیام‌های قدیمیِ بدون شناسهٔ ثبت‌شده، روی خود پیام در گروه ریپلای کنید و بنویسید:\n/invoicecleanup شماره‌فاکتور",
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
            if(action=="choose"&&d.Original is not null)
            {await ShowInvoiceCancellationSelectionAsync(chat,callback.From.Id,d with{Preview=d.Original,Index=0,Page=0},ct);return;}
            if(d.Step=="select"&&action.EndsWith(":"+d.Token,StringComparison.Ordinal))
            {
                var selected=(d.Selected??[]).ToHashSet();var selection=action[..^(d.Token!.Length+1)];
                if(selection=="all")selected=d.Preview.Lines.Select(l=>l.ItemId).ToHashSet();
                else if(selection.StartsWith("pick:")&&int.TryParse(selection[5..],out var index)&&index>=0&&index<d.Preview.Lines.Length)
                {var id=d.Preview.Lines[index].ItemId;if(!selected.Add(id))selected.Remove(id);}
                else if(selection.StartsWith("page:")&&int.TryParse(selection[5..],out var page)&&page>=0&&page<=(d.Preview.Lines.Length-1)/10)
                {await ShowInvoiceCancellationSelectionAsync(chat,callback.From.Id,d with{Page=page},ct);return;}
                else if(selection=="next")
                {
                    var preview=await new InvoiceCancellationService(_db).SelectAsync(d.Preview,d.Returned,selected.Order().ToArray(),ct);
                    d=d with{Preview=preview,Selected=selected.Order().ToArray(),Index=0,Prices=preview.Lines.ToDictionary(l=>l.ItemId,l=>d.Prices?.GetValueOrDefault(l.ItemId,l.CurrentPrice)??l.CurrentPrice)};
                    InvoiceCancelDrafts[key]=d;
                    if(d.Returned)await ShowInvoiceReturnPriceAsync(chat,callback.From.Id,d,ct);else await ShowInvoiceCancellationConfirmationAsync(chat,callback.From.Id,d,ct);
                    return;
                }
                else throw new InvalidOperationException("انتخاب معتبر نیست؛ از دکمه‌های همین مرحله استفاده کنید.");
                await ShowInvoiceCancellationSelectionAsync(chat,callback.From.Id,d with{Selected=selected.Order().ToArray()},ct);return;
            }
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
                await _sender.SendInlineKeyboardAsync(chat.ToString(),(result.SourceInvoiceId.HasValue?$"✅ آیتم‌های انتخاب‌شده از فاکتور {d.Preview.Number} حذف شدند؛ سایر آیتم‌ها باقی ماندند.\nمبلغ فاکتور باقی‌مانده: {result.RemainingAmount:N0} تومان\nنسخهٔ اصلاح‌شده با کپشن جدید در صف ارسال است.\n":$"✅ فاکتور {d.Preview.Number} برای {d.Preview.Customer} لغو شد.\n")+$"مبلغ اصلاح‌شدهٔ بدهی: {result.ReversedAmount:N0} تومان\n"+
                    (result.Returned?$"{result.Codes.Length} بستهٔ موجودی ثبت شد؛ کدها: {string.Join("، ",result.Codes)}\nانتشار با عکس در کانال موجودی در حال انجام است.":"موجودی جدیدی ایجاد نشد.")+"\nپاکسازی پیام‌های قدیمی از صف پیگیری می‌شود؛ در حذف جزئی، بعد از تحویل موفق نسخهٔ اصلاح‌شده انجام می‌شود.",
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
                var service=new InvoiceCancellationService(_db);var preview=await service.ResolveForSelectionAsync(text,d.Returned,ct);
                d=d with{Preview=preview,Original=preview,Prices=preview.Lines.ToDictionary(l=>l.ItemId,l=>l.CurrentPrice),Index=0,Selected=[]};
                InvoiceCancelDrafts[key]=d;
                if(preview.Lines.Length>1){await ShowInvoiceCancellationSelectionAsync(message.Chat.Id,message.From.Id,d,ct);return true;}
                preview=await service.SelectAsync(preview,d.Returned,preview.Lines.Select(l=>l.ItemId).ToArray(),ct);
                d=d with{Preview=preview,Selected=preview.Lines.Select(l=>l.ItemId).ToArray()};InvoiceCancelDrafts[key]=d;
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
    private async Task ShowInvoiceCancellationSelectionAsync(long chat,long user,InvoiceCancelDraft d,CancellationToken ct)
    {
        var p=d.Preview!;var token=Guid.NewGuid().ToString("N")[..10];var selected=(d.Selected??[]).ToHashSet();
        InvoiceCancelDrafts[(chat,user)]=d with{Step="select",Token=token};
        var buttons=new List<IReadOnlyCollection<TelegramInlineButton>>{new[]{new TelegramInlineButton("✅ انتخاب همهٔ موارد",InvoiceCancelPrefix+"all:"+token)}};
        foreach(var (line,index) in p.Lines.Select((l,i)=>(l,i)).Skip(d.Page*10).Take(10))
        {
            var name=line.Name.Length>30?line.Name[..30]+"…":line.Name;
            buttons.Add([new TelegramInlineButton($"{(selected.Contains(line.ItemId)?"☑️":"⬜️")} آیتم {index+1}: {name} | {line.Volume:0.#} میل",InvoiceCancelPrefix+$"pick:{index}:"+token)]);
        }
        var paging=new List<TelegramInlineButton>();if(d.Page>0)paging.Add(new("⬅️ قبلی",InvoiceCancelPrefix+$"page:{d.Page-1}:"+token));
        if((d.Page+1)*10<p.Lines.Length)paging.Add(new("بعدی ➡️",InvoiceCancelPrefix+$"page:{d.Page+1}:"+token));if(paging.Count>0)buttons.Add(paging);
        buttons.Add([new("✅ ادامه با موارد انتخاب‌شده",InvoiceCancelPrefix+"next:"+token)]);buttons.Add([new("❌ انصراف",InvoiceCancelPrefix+"cancel")]);
        await _sender.SendInlineKeyboardAsync(chat.ToString(),$"🧾 فاکتور {p.Number}\nمشتری: {p.Customer}\nکدام موارد {(d.Returned?"حذف و به موجودی منتقل شوند":"حذف شوند")}؟\nیک یا چند آیتم را انتخاب کنید، یا «همهٔ موارد» را بزنید.\nتعداد انتخاب‌شده: {selected.Count} از {p.Lines.Length}\nآیتم‌های انتخاب‌نشده در فاکتور می‌مانند. هنوز تغییری ثبت نشده است.",buttons,ct);
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
        var scope=p.Partial?$"فقط آیتم‌های بالا حذف می‌شوند. شمارهٔ فاکتور باقی‌مانده حفظ و مبلغش {p.RemainingAmount:N0} تومان می‌شود؛ نسخهٔ اصلاح‌شده با کپشن جدید برای مشتری ارسال می‌شود.":"کل فاکتور لغو و مبلغ آن از بدهی مشتری کسر می‌شود.";
        await _sender.SendInlineKeyboardAsync(chat.ToString(),scope+(d.Returned?" آیتم‌های انتخاب‌شده به موجودی آمادهٔ ارسال منتقل و با عکس در کانال موجودی منتشر می‌شوند؛ تخفیف قبلی خودکار به قیمت موجودی منتقل نمی‌شود.":" موجودی جدیدی ساخته نمی‌شود."),
            [[new TelegramInlineButton(d.Returned?"✅ تأیید حذف موارد و ارسال به موجودی":"✅ تأیید حذف موارد انتخاب‌شده",InvoiceCancelPrefix+"confirm:"+token)],[new TelegramInlineButton("↩️ تغییر انتخاب آیتم‌ها",InvoiceCancelPrefix+"choose")],[new TelegramInlineButton("❌ انصراف",InvoiceCancelPrefix+"cancel")]],ct);
    }
}
