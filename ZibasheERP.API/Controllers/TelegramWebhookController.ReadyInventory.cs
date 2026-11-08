using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Inventory;
using ZibasheERP.API.Marketing;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private sealed record InventoryDraft(Guid Id,string Step,DateTime Until,Guid? Reference=null,string Name="",string Photo="",
        decimal Price=0,decimal Volume=0,Guid Bottle=default,decimal BottlePrice=0,string Position="",string? Token=null,string Search="",Guid? PositionList=null,string? PhotoReturnStep=null,
        Guid? PurchaseList=null,string BuyerIdentity="",string BuyerUserId="",string? BuyerUsername=null,Guid? BuyerCustomerId=null,Guid? PromotionId=null,Guid[]? InvoiceLists=null,Guid[]? InvoiceRequests=null,
        string Description="",bool DescriptionCollected=false,bool SoldPosition=false,string? ExpectedPosition=null,Guid[]? PositionChoices=null);
    private static readonly ConcurrentDictionary<(long Chat,long User),InventoryDraft> InventoryDrafts=new();
    private static string InvAction(string action)=>"invoiceadmin:ready:"+action;
    internal static string ReadyInventoryCaption(SalesList list)=>ReadyInventoryPresentation.Caption(list);
    internal Task RefreshReadyInventoryPostAsync(Guid id,CancellationToken ct)=>RefreshChannelSalesListAsync(id,ct);
    private async Task RemoveInvoicedReadyInventoryPostAsync(SalesList list,CancellationToken ct)
    {
        if(!list.IsInventoryOffer||list.InventoryCode is null||list.Status!=SalesListStatus.Invoiced||
            !list.TelegramMessageId.HasValue||string.IsNullOrWhiteSpace(list.TelegramChannelId))return;
        var messageId=list.TelegramMessageId.Value;
        var chat=list.TelegramChannelId;
        var binding=ReadyInventoryRemoval.Binding(list.Id,chat,messageId);
        if(await _db.NotificationOutbox.AsNoTracking().AnyAsync(n=>!n.IsDeleted&&n.Channel==ReadyInventoryRemoval.RetirementChannel&&
            n.Recipient==binding&&n.Status==NotificationOutboxStatus.Processed,ct))
        {await ReadyInventoryRemoval.QueueManualAlertAsync(_db,list.Id,_options.InvoiceFailureChatId,true,true,ct);return;}
        var result=await _sender.DeleteMessageOnceAsync(chat,messageId,ct);
        if(result.IsRetryable)throw new InventoryPostRetryException(result.RetryAfterSeconds??60);
        if(!result.IsSuccessful&&result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
        {
            var restricted=result.Error?.Contains("message can't be deleted",StringComparison.OrdinalIgnoreCase)==true||
                result.Error?.Contains("message cannot be deleted",StringComparison.OrdinalIgnoreCase)==true||
                result.Error?.Contains("not enough rights",StringComparison.OrdinalIgnoreCase)==true;
            if(!restricted)throw new InvalidOperationException("TelegramInventoryPostDeleteRetryRequired");
            // Telegram normally refuses deletion after 48 hours even for channel
            // administrators. Withdraw the exact old offer instead; never undo
            // successful billing or publish a replacement sold advertisement.
            var name=string.IsNullOrWhiteSpace(list.PersianName)?list.EnglishName:list.PersianName;
            var caption=$"✅ فروخته شد\n🧴 {name}\n🔖 کد موجودی: {list.DisplayCode}\nاین بسته فاکتور شده و امکان ثبت خرید ندارد.";
            var edited=!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)
                ?await _sender.EditCaptionWithKeyboardAsync(chat,messageId,caption,[],ct)
                :await _sender.EditTextWithKeyboardAsync(chat,messageId,caption,[],ct);
            if(!edited.IsSuccessful&&edited.Error?.Contains("there is no text",StringComparison.OrdinalIgnoreCase)==true)
                edited=await _sender.EditCaptionWithKeyboardAsync(chat,messageId,caption,[],ct);
            if(edited.IsSuccessful||IsTelegramMessageUnchanged(edited.Error))
            {
                // Retain the actual message ID for manual deletion and audit.
                // A late receipt for a different post has a different binding.
                var id=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("inventory-post-retired:"+binding))[..16]);
                if(!await _db.NotificationOutbox.AnyAsync(n=>n.Id==id,ct))
                {
                    _db.NotificationOutbox.Add(new(){Id=id,Channel=ReadyInventoryRemoval.RetirementChannel,EventType="SoldPostWithdrawnDeletionRestricted",
                        Recipient=binding,Payload=JsonSerializer.Serialize(new{SalesListId=list.Id,ChatId=chat,MessageId=messageId,Outcome="withdrawn",ManualDeletionRequired=true}),
                        Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});
                    await _db.SaveChangesAsync(ct);
                }
                await ReadyInventoryRemoval.QueueManualAlertAsync(_db,list.Id,_options.InvoiceFailureChatId,true,true,ct);
                return;
            }
            if(edited.Error?.Contains("message to edit not found",StringComparison.OrdinalIgnoreCase)!=true)
            {
                await ReadyInventoryRemoval.QueueManualAlertAsync(_db,list.Id,_options.InvoiceFailureChatId,false,true,ct);
                if(edited.RetryAfterSeconds is >0)throw new InventoryPostRetryException(edited.RetryAfterSeconds.Value);
                throw new InvalidOperationException("TelegramInventoryPostWithdrawalRetryRequired");
            }
        }
        // A late publication receipt must never have its newer message cleared.
        await _db.SalesLists.Where(value=>value.Id==list.Id&&value.Status==SalesListStatus.Invoiced&&
                value.TelegramMessageId==messageId&&value.TelegramChannelId==chat)
            .ExecuteUpdateAsync(value=>value.SetProperty(row=>row.TelegramMessageId,(long?)null)
                .SetProperty(row=>row.UpdatedAt,DateTime.UtcNow),ct);
        var tracked=_db.ChangeTracker.Entries<SalesList>().FirstOrDefault(entry=>entry.Entity.Id==list.Id);
        if(tracked is not null)await tracked.ReloadAsync(ct);
    }
    internal static string InventoryReferenceName(string? persian,string? catalogue,string? english,int code)
    {
        static bool Known(string? text)=>!string.IsNullOrWhiteSpace(text)&&text.Trim() is not("نامشخص" or "عطر" or "Unknown" or "unknown" or "-" or "?");
        var names=new[]{persian,catalogue,english};
        return names.FirstOrDefault(v=>Known(v)&&v!.Any(c=>c is >= '\u0600' and <= '\u06FF'))?.Trim()
            ?? names.FirstOrDefault(Known)?.Trim() ?? $"لیست {code}";
    }
    private async Task ShowInventoryReferencePhotoAsync(long chat,InventoryDraft draft,CancellationToken ct)
    {
        var prompt=draft.Step=="price"?"قیمت هر میل را به تومان وارد کنید.":"چند میل موجودی آماده دارید؟ حجم را به میل وارد کنید.";
        await _sender.SendPhotoWithKeyboardAsync(chat.ToString(),draft.Photo,$"🧴 <b>{Html(draft.Name)}</b>\n📷 پیش‌نمایش عکس موجودی\n\n{prompt}",
            [[new TelegramInlineButton("🖼 تغییر عکس",InvAction("reference-photo"))],[new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))]],ct);
    }
    private Task InvPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))]],ct);
    private async Task HandleReadyInventoryCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var key=(chat,callback.From.Id);
        if(callback.Message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"این منو فقط برای ادمین‌های مجاز در گفت‌وگوی خصوصی ربات است.",ct,true);return;}
        var action=callback.Data!["invoiceadmin:ready:".Length..];
        if((action=="finance"||action.StartsWith("finance:",StringComparison.Ordinal))&&!IsPrimaryOwner(callback.From.Id))
        {await _sender.AnswerCallbackAsync(callback.Id,"گزارش مالی موجودی فقط برای مدیر تعیین‌شده قابل مشاهده است.",ct,true);return;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        try
        {
            if(action=="finance")
            {
                await _sender.SendInlineKeyboardAsync(chat.ToString(),"💰 گزارش مالی موجودی\nبازهٔ فروش را انتخاب کنید:",
                    new[]{7,15,30}.Select(day=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton(day==7?"هفتهٔ گذشته":$"{day} روز گذشته",InvAction("finance:"+day))})
                    .Append(new[]{new TelegramInlineButton("↩️ منوی مدیر","invoiceadmin:menu:main")}).ToArray(),ct);return;
            }
            if(action.StartsWith("finance:",StringComparison.Ordinal))
            {
                if(!int.TryParse(action[8..],out var financialDays))throw new InvalidOperationException("بازهٔ گزارش مالی معتبر نیست.");
                var r=await new ReadyInventoryService(_db).FinancialReportAsync(financialDays,ct);
                var from=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(r.FromUtc,"Asia/Tehran");
                var until=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(r.UntilUtc,"Asia/Tehran");
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"💰 گزارش مالی موجودی — {r.Days} روز گذشته\nاز {from:yyyy/MM/dd HH:mm}\nتا {until:yyyy/MM/dd HH:mm} — تهران\n\n🧾 فاکتور شده: {r.SalesAmount:N0} تومان\nتعداد: {r.SoldPackages} بسته\n\n✅ پرداخت شده: {r.ReceivedAmount:N0} تومان\n⏳ در انتظار پرداخت: {r.OutstandingAmount:N0} تومان\n\n📦 فروش نرفته: {r.UnsoldValue:N0} تومان\nتعداد موجودی فعلی: {r.UnsoldPackages} بسته\nاز این تعداد، رزرو و منتظر فاکتور: {r.ReservedPackages} بسته\nارزش بسته‌های رزروشده: {r.ReservedValue:N0} تومان\n\nفروش بر اساس تاریخ صدور فاکتور، با مبلغ واقعی پس از تخفیف و با احتساب شیشه است؛ لغوشده‌ها محاسبه نمی‌شوند. وصول و مانده، وضعیت فعلی همین فاکتورهاست.\nارزش فروش‌نرفته‌ها به قیمت فعلی عطر و شیشه و مستقل از بازهٔ ثبت موجودی محاسبه می‌شود؛ این گزارش سود نیست.",
                    [[new TelegramInlineButton("🔄 انتخاب بازه",InvAction("finance"))],[new TelegramInlineButton("↩️ منوی مدیر","invoiceadmin:menu:main")]],ct);return;
            }
            if(await TryHandleInventoryPurchaseCallbackAsync(callback,action,ct))return;
            if(await TryHandleInventoryLocationCallbackAsync(callback,action,ct))return;
            if(action=="sold-position")
            {
                ClearAdminWorkflowDrafts(chat,callback.From.Id);
                InventoryDrafts[key]=new(Guid.NewGuid(),"sold-search",DateTime.UtcNow.AddMinutes(30),SoldPosition:true);
                await InvPrompt(chat,"نام فارسی/انگلیسی عطر یا کد لیست/موجودی فروش‌رفته را وارد کنید.\nفقط جایگاه نگهداری ثبت می‌شود و پستی در کانال منتشر نمی‌شود.",ct);return;
            }
            if(action.StartsWith("pick-sold:")&&Guid.TryParseExact(action[10..],"N",out var soldId))
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="sold-search"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("جست‌وجو منقضی شده است.");
                var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==soldId&&!v.IsDeleted,ct)??throw new InvalidOperationException("عطر پیدا نشد.");
                InventoryDrafts[key]=d with{Step="sold-position",PositionList=list.Id};
                await InvPrompt(chat,$"{InventoryReferenceName(list.PersianName,null,list.EnglishName,list.DisplayCode)} — کد {list.DisplayCode}\nجایگاه فعلی: {list.StoragePosition??"ثبت نشده"}\nنام یا کد جایگاه فیزیکی را وارد کنید.",ct);return;
            }
            if(action.StartsWith("move-sold:")&&Guid.TryParseExact(action[10..],"N",out var soldMoveId))
            {
                var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==soldMoveId&&!v.IsDeleted,ct)??throw new InvalidOperationException("عطر پیدا نشد.");
                InventoryDrafts[key]=new(Guid.NewGuid(),"sold-position",DateTime.UtcNow.AddMinutes(30),PositionList:list.Id,SoldPosition:true);
                await InvPrompt(chat,$"کد {list.DisplayCode}\nجایگاه فعلی: {list.StoragePosition??"ثبت نشده"}\nجایگاه فیزیکی جدید را وارد کنید.",ct);return;
            }
            if(action is "new" or "manual" or "find" or "position")
            {
                ClearAdminWorkflowDrafts(chat,callback.From.Id);
                var step=action=="manual"?"name":action=="position"?"position-search":"search";
                InventoryDrafts[key]=new(Guid.NewGuid(),step,DateTime.UtcNow.AddMinutes(30));
                await InvPrompt(chat,step=="name"?"نام عطر را وارد کنید.":step=="search"?"کد لیست یا نام فارسی/انگلیسی عطر را وارد کنید؛ قسمتی از نام هم کافی است.":"نام عطر، کد موجودی یا کد لیست مرجع را برای پیدا کردن جایگاه وارد کنید.",ct);return;
            }
            if(action.StartsWith("pick:")&&Guid.TryParseExact(action[5..],"N",out var referenceId))
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="search"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("جست‌وجو منقضی شده است.");
                var list=await _db.SalesLists.AsNoTracking().Include(v=>v.Perfume).SingleOrDefaultAsync(v=>v.Id==referenceId&&!v.IsDeleted&&!v.IsInventoryOffer,ct)
                    ??throw new InvalidOperationException("لیست مرجع پیدا نشد.");
                var photo=await SalesListPhotoResolver.ResolveSourcePostAsync(_db,list,ct);
                if(string.IsNullOrWhiteSpace(photo)&&list.TelegramMessageId.HasValue&&!string.IsNullOrWhiteSpace(list.TelegramChannelId))
                {
                    var source=await _sender.ReadSourcePhotoAsync(chat.ToString(),list.TelegramChannelId,list.TelegramMessageId.Value,ct);
                    if(source.IsSuccessful)photo=source.ExternalFileId;
                }
                var nextStep=list.PricePerMl>0?"volume":"price";
                var next=d with{Reference=list.Id,Name=InventoryReferenceName(list.PersianName,list.Perfume?.Name,list.EnglishName,list.DisplayCode),
                    Photo=photo??"",Price=list.PricePerMl,Step=string.IsNullOrWhiteSpace(photo)?"photo":nextStep,PhotoReturnStep=string.IsNullOrWhiteSpace(photo)?nextStep:null};
                InventoryDrafts[key]=next;
                if(!string.IsNullOrWhiteSpace(photo))await ShowInventoryReferencePhotoAsync(chat,next,ct);
                else await InvPrompt(chat,"عکس این پست از تلگرام قابل دریافت نبود؛ فقط در این حالت عکس همان عطر را ارسال کنید یا به منو برگردید.",ct);
                return;

            }
            if(action=="reference-photo")
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Reference is null||d.Step is not("volume" or "price")||d.Until<DateTime.UtcNow)
                    throw new InvalidOperationException("این پیش‌نمایش جایگزین شده است؛ از دکمهٔ تغییر عکس زیر پیش‌نمایش فعلی استفاده کنید.");
                InventoryDrafts[key]=d with{Step="photo",PhotoReturnStep=d.Step};await InvPrompt(chat,"عکس جدید عطر را ارسال کنید.",ct);return;
            }
            if(action=="original-bottle")
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="bottle"||d.Until<DateTime.UtcNow)
                    throw new InvalidOperationException("انتخاب شیشه منقضی شده است.");
                InventoryDrafts[key]=d with{Bottle=ReadyInventoryService.OriginalBottleId,BottlePrice=0,Step="position"};
                await InvPositionPrompt(chat,ct);return;
            }
            if(action.StartsWith("bottle:")&&Guid.TryParseExact(action[7..],"N",out var bottleId))
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="bottle"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("انتخاب شیشه منقضی شده است.");
                var bottle=await _db.Bottles.SingleOrDefaultAsync(v=>v.Id==bottleId&&!v.IsDeleted&&v.IsActive&&v.VolumeMl>=d.Volume,ct)??throw new InvalidOperationException("شیشه معتبر نیست.");
                InventoryDrafts[key]=d with{Bottle=bottle.Id,BottlePrice=bottle.SalePrice,Step="bottle-price"};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"قیمت شیشه: {bottle.SalePrice:N0} تومان\nمبلغ دیگری وارد کنید یا همین قیمت را تأیید کنید.",
                    [[new TelegramInlineButton("✅ همین قیمت",InvAction("default-bottle-price"))],[new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))]],ct);return;
            }
            if(action=="default-bottle-price")
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="bottle-price"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("انتخاب منقضی شده است.");
                InventoryDrafts[key]=d with{Step="position"};await InvPositionPrompt(chat,ct);return;
            }
            if(action.StartsWith("edit:"))
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="confirm"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("پیش‌نمایش منقضی شده است.");
                var field=action[5..];if(field is not("name" or "photo" or "price" or "volume" or "position" or "description"))return;
                InventoryDrafts[key]=d with{Step=field,Token=null};
                if(field=="description"){await InvDescriptionPrompt(chat,ct);return;}
                await InvPrompt(chat,field=="photo"?"عکس جدید عطر را ارسال کنید.":field=="price"?"قیمت هر میل عطر را به تومان وارد کنید.":field=="volume"?"حجم بسته را به میل وارد کنید.":field=="position"?"نام یا کد جایگاه فیزیکی را وارد کنید.":"نام عطر را وارد کنید.",ct);return;
            }
            if(action.StartsWith("save:"))
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="confirm"||d.Token!=action[5..]||d.Until<DateTime.UtcNow)throw new InvalidOperationException("این پیش‌نمایش منقضی یا جایگزین شده است.");
                if(string.IsNullOrWhiteSpace(_options.ReadyInventoryChannelId))throw new InvalidOperationException("مقصد کانال موجودی تنظیم نشده است.");
                if(!d.DescriptionCollected)throw new InvalidOperationException("مرحلهٔ توضیحات را تکمیل کنید یا بدون توضیحات را بزنید.");
                var list=await new ReadyInventoryService(_db).CreateAsync(new(d.Id,d.Reference,d.Name,d.Photo,d.Price,d.Volume,d.Bottle,d.BottlePrice,d.Position,callback.From.Id,d.Description),ct);
                await QueueFlashAsync(list.Id,new MarketingPromotionJob(list.Id,list.DisplayCode,"inventory-publish",chat,callback.From.Id),ct);
                InventoryDrafts.TryRemove(key,out _);await InvPrompt(chat,$"✅ موجودی {list.DisplayCode} ثبت شد؛ انتشار در مقصد موجودی در حال انجام است.\nجایگاه داخلی: {list.StoragePosition}",ct);return;
            }
            if(action=="skip-description")
            {
                if(!InventoryDrafts.TryGetValue(key,out var d)||d.Step!="description"||d.Until<DateTime.UtcNow)throw new InvalidOperationException("مرحلهٔ توضیحات منقضی شده است.");
                await InvPreview(chat,callback.From.Id,d with{Description="",DescriptionCollected=true},ct);return;
            }
            if(action.StartsWith("move:")&&Guid.TryParseExact(action[5..],"N",out var moveId))
            {
                var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==moveId&&!v.IsDeleted&&v.IsInventoryOffer,ct)??throw new InvalidOperationException("موجودی پیدا نشد.");
                InventoryDrafts[key]=new(Guid.NewGuid(),"move",DateTime.UtcNow.AddMinutes(30),PositionList:list.Id);
                await InvPrompt(chat,$"کد موجودی {list.DisplayCode}\nجایگاه فعلی: {list.StoragePosition??"ثبت نشده"}\nجایگاه فیزیکی جدید را وارد کنید.",ct);return;
            }
            if(action=="requests"){await InvRequests(chat,ct);return;}
            if(action.StartsWith("approve-bundle:"))
            {
                if(!InventoryDrafts.TryGetValue(key,out var draft)||draft.Step!="invoice-bundle"||draft.Token!=action[15..]||draft.Until<DateTime.UtcNow||draft.InvoiceLists is not {Length:>0})
                    throw new InvalidOperationException("پیش‌نمایش فاکتور منقضی یا جایگزین شده؛ درخواست‌ها را دوباره بررسی کنید.");
                var result=await _invoiceIssuanceService.IssueCompletedListsAsync(draft.InvoiceLists,callback.From.Id.ToString(),ct,draft.InvoiceRequests);
                InventoryDrafts.TryRemove(key,out _);
                // Invoice issuance has already committed its durable removal jobs.
                // A Telegram presentation failure must not look like failed billing.
                await InvPrompt(chat,$"✅ فاکتور تجمیعی {draft.InvoiceLists.Length} آیتم موجودی صادر شد.\n{string.Join("، ",result.InvoiceNumbers)}\nارسال فاکتور به مشتری، نسخهٔ گروه واریز جدید و خروج پست‌ها از فروش از صف پایدار انجام می‌شود.\nاگر تلگرام حذف پست قدیمی را نپذیرد، پست «فروخته شد» می‌شود و دکمهٔ خریدش برداشته می‌شود؛ حذف کامل آن با ادمین کانال است.",ct);return;
            }
            if((action.StartsWith("review:")||action.StartsWith("approve:")||action.StartsWith("reject:"))&&Guid.TryParseExact(action[(action.IndexOf(':')+1)..],"N",out var listId))
            {
                var list=await _db.SalesLists.AsNoTracking().Include(v=>v.Requests).SingleOrDefaultAsync(v=>v.Id==listId&&v.IsInventoryOffer&&v.InventoryCode!=null&&!v.IsDeleted,ct)??throw new InvalidOperationException("موجودی پیدا نشد.");
                var request=list.Requests.SingleOrDefault(v=>!v.IsDeleted&&v.Kind==SalesListRequestKind.CurrentBottle&&v.Status==SalesListRequestStatus.Confirmed);
                if(request is null)throw new InvalidOperationException("درخواست خرید فعالی برای این موجودی نیست.");
                if(action.StartsWith("reject:"))
                {await new ReadyInventoryService(_db).RejectAsync(list.Id,ct);await RefreshChannelSalesListAsync(list.Id,ct);await InvPrompt(chat,"درخواست رد شد و موجودی دوباره قابل خرید است.",ct);return;}
                var pending=await _db.SalesListRequests.AsNoTracking().Include(v=>v.SalesList)
                    .Where(v=>!v.IsDeleted&&v.Status==SalesListRequestStatus.Confirmed&&v.Kind==SalesListRequestKind.CurrentBottle&&
                        v.TelegramUserId==request.TelegramUserId&&v.SalesList!=null&&!v.SalesList.IsDeleted&&v.SalesList.IsInventoryOffer&&
                        v.SalesList.InventoryCode!=null&&v.SalesList.Status==SalesListStatus.Full)
                    .OrderBy(v=>v.CreatedAt).ToArrayAsync(ct);
                if(pending.Length==0)throw new InvalidOperationException("درخواست‌ها تغییر کرده‌اند؛ دوباره بررسی کنید.");
                var token=Guid.NewGuid().ToString("N")[..16];
                InventoryDrafts[key]=new(Guid.NewGuid(),"invoice-bundle",DateTime.UtcNow.AddMinutes(30),Token:token,InvoiceLists:pending.Select(v=>v.SalesListId).ToArray(),InvoiceRequests:pending.Select(v=>v.Id).ToArray());
                var lines=pending.Select(v=>$"کد موجودی {v.SalesList!.DisplayCode} — {(string.IsNullOrWhiteSpace(v.SalesList.PersianName)?v.SalesList.EnglishName:v.SalesList.PersianName)}\n{v.VolumeMl:0.#} میل | {v.PerfumePricePerMl*v.VolumeMl+v.BottlePrice:N0} تومان\nجایگاه داخلی: {v.SalesList.StoragePosition??"ثبت نشده"}");
                var text=$"🧾 بررسی فاکتور تجمیعی موجودی\nمشتری: @{request.TelegramUsername??request.TelegramUserId}\nتعداد آیتم: {pending.Length}\n\n"+string.Join("\n\n",lines)+$"\n\nمبلغ کل: {pending.Sum(v=>v.PerfumePricePerMl*v.VolumeMl+v.BottlePrice):N0} تومان\nبا تأیید، همهٔ آیتم‌های بالا در یک فاکتور صادر می‌شوند. درخواست‌های جدید پس از این پیش‌نمایش در فاکتور دیگری بررسی می‌شوند.";
                // Send the full item breakdown in bounded messages, keeping approval after every item.
                for(var offset=0;offset<text.Length;offset+=3500)await InvPrompt(chat,text.Substring(offset,Math.Min(3500,text.Length-offset)),ct);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),"آیتم‌های بالا تأیید شوند؟",[[new TelegramInlineButton("✅ تأیید و صدور فاکتور تجمیعی",InvAction("approve-bundle:"+token))],
                    [new TelegramInlineButton($"❌ رد فقط موجودی {list.DisplayCode}",InvAction("reject:"+list.Id.ToString("N")))],[new TelegramInlineButton("↩️ درخواست‌ها",InvAction("requests"))]],ct);return;
            }
            if(action=="reports")
            {await _sender.SendInlineKeyboardAsync(chat.ToString(),"بازهٔ گزارش موجودی را انتخاب کنید:",new[]{10,20,30,-30}.Select(day=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton(day<0?"قدیمی‌تر از ۳۰ روز":$"{day} روز گذشته",InvAction("report:"+day))}).Append(new[]{new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))}).ToArray(),ct);return;}
            if(action.StartsWith("report:")&&int.TryParse(action[7..],out var days))
            {
                var report=await new ReadyInventoryService(_db).ReportAsync(days,ct);
                var now=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow,"Asia/Tehran");
                await InvPrompt(chat,$"📊 گزارش موجودی — {(days<0?"قدیمی‌تر از ۳۰ روز":$"{days} روز گذشته")}\nتا: {now:yyyy/MM/dd HH:mm}\n\nثبت‌شده: {report.RegisteredPackages} بسته / {report.DistinctPerfumes} عطر\nفاکتورشده: {report.InvoicedPackages} بسته / {report.Invoices} فاکتور\nدر انتظار پرداخت: {report.UnpaidPackages} بسته / {report.UnpaidInvoices} فاکتور\nماندهٔ پرداخت موجودی: {report.Outstanding:N0} تومان\n\nثبت بر اساس تاریخ ورود موجودی؛ فروش و بدهی بر اساس تاریخ صدور فاکتور و وضعیت فعلی پرداخت است.",ct);return;
            }
            InventoryDrafts.TryRemove(key,out _);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"📦 موجودی آماده\nهمهٔ بسته‌ها آمادهٔ ارسال هستند؛ پرداخت جداگانه بررسی می‌شود.",
                [[new TelegramInlineButton("🛍 ثبت خرید برای مشتری",InvAction("buy"))],
                 [new TelegramInlineButton("➕ ثبت از لیست کانال",InvAction("new"))],[new TelegramInlineButton("✍️ ثبت دستی",InvAction("manual"))],
                 [new TelegramInlineButton("📦 ثبت موجودی فروش‌رفته",InvAction("sold-position"))],
                 [new TelegramInlineButton("📍 ثبت جایگاه موجودی",InvAction("register-position"))],
                 [new TelegramInlineButton("📍 جست‌وجوی جایگاه / مدیریت موجودی",InvAction("position"))],[new TelegramInlineButton("🧾 موجودی‌های درخواست‌شده",InvAction("requests"))],
                 [new TelegramInlineButton("📊 گزارش موجودی",InvAction("reports"))],[new TelegramInlineButton("↩️ مدیریت", "invoiceadmin:menu:main")]],ct);
        }
        catch(InvalidOperationException e){await InvPrompt(chat,e.Message,ct);}
    }
    private static Guid InventoryReportJobId(Guid batch,Guid list)=>new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"inventory-report:{batch}:{list}"))[..16]);
    private async Task InvRequests(long chat,CancellationToken ct)
    {
        var lists=await _db.SalesLists.AsNoTracking().Where(v=>v.InventoryCode!=null&&!v.IsDeleted&&v.Status==SalesListStatus.Full&&v.Requests.Any(r=>!r.IsDeleted&&r.Status==SalesListRequestStatus.Confirmed))
            .OrderBy(v=>v.UpdatedAt).Take(40).Select(v=>new{v.Id,v.InventoryCode,v.EnglishName}).ToArrayAsync(ct);
        await _sender.SendInlineKeyboardAsync(chat.ToString(),lists.Length==0?"درخواست موجودی در انتظار بررسی نیست.":"🧾 موجودی‌های درخواست‌شده؛ برای بررسی و صدور فاکتور انتخاب کنید.",
            lists.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{v.InventoryCode} — {v.EnglishName}",InvAction("review:"+v.Id.ToString("N")))}).Append(new[]{new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))}).ToArray(),ct);
    }
    private async Task InvPositionPrompt(long chat,CancellationToken ct)
    {
        var positions=await _db.SalesLists.AsNoTracking().Where(v=>v.IsInventoryOffer&&!v.IsDeleted&&v.StoragePosition!=null).Select(v=>v.StoragePosition!).Distinct().OrderBy(v=>v).Take(8).ToArrayAsync(ct);
        await InvPrompt(chat,"این بسته در کدام جایگاه فیزیکی نگهداری می‌شود؟ نام یا کد را وارد کنید؛ مثال: قفسهٔ ۲، جایگاه ۳.\nاین اطلاعات فقط برای ادمین‌هاست."+(positions.Length==0?"":"\nجایگاه‌های قبلی:\n"+string.Join("\n",positions)),ct);
    }
    private Task InvDescriptionPrompt(long chat,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),
        "📝 توضیحات اختیاری موجودی\nاگر توضیحی دارید بنویسید؛ این متن به مشتری‌ها در پست موجودی نمایش داده می‌شود.\nحداکثر ۴۰۰ کاراکتر؛ یا «بدون توضیحات» را بزنید.",
        [[new TelegramInlineButton("⏭ بدون توضیحات",InvAction("skip-description"))],[new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))]],ct);
    private async Task InvDescriptionOrPreview(long chat,long user,InventoryDraft d,CancellationToken ct)
    {
        if(d.DescriptionCollected){await InvPreview(chat,user,d,ct);return;}
        InventoryDrafts[(chat,user)]=d with{Step="description",Token=null};await InvDescriptionPrompt(chat,ct);
    }
    private async Task<bool> TryHandleReadyInventoryMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!InventoryDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var d))return false;
        var key=(message.Chat.Id,message.From.Id);var chat=message.Chat.Id;
        if(message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct)){InventoryDrafts.TryRemove(key,out _);return false;}
        if(d.Until<DateTime.UtcNow){InventoryDrafts.TryRemove(key,out _);await InvPrompt(chat,"فرایند منقضی شد؛ دوباره شروع کنید.",ct);return true;}
        var text=message.Text?.Trim()??"";
        if(text.StartsWith('/')){InventoryDrafts.TryRemove(key,out _);return false;}
        var numeric=NormalizeNumber(text).Replace("٬","").Replace(",","").Replace('٫','.');
        try
        {
            if(d.Step.StartsWith("buy-",StringComparison.Ordinal))return await HandleInventoryPurchaseMessageAsync(message,d,ct);
            if(d.Step.StartsWith("location-",StringComparison.Ordinal))return await HandleInventoryLocationMessageAsync(message,d,ct);
            if(d.Step is "search" or "position-search" or "sold-search")
            {
                if(text.Length is <1 or >150)throw new InvalidOperationException("نام یا کد معتبر وارد کنید.");
                int.TryParse(numeric,out var code);var normalized=text.Replace('ي','ی').Replace('ك','ک');
                if(d.Step=="sold-search")
                {
                    var found=await _db.SalesLists.AsNoTracking().Include(v=>v.Perfume).Where(v=>!v.IsDeleted&&
                        (v.InventoryCode==code||v.PublicCode==code||v.StablePublicCode==code||v.PersianName.Contains(normalized)||v.EnglishName.Contains(normalized)||v.Perfume.Name.Contains(normalized)))
                        .OrderByDescending(v=>v.OpenDate).Take(30).ToArrayAsync(ct);
                    await _sender.SendInlineKeyboardAsync(chat.ToString(),found.Length==0?"عطر در سوابق پیدا نشد؛ نام یا کد دیگری وارد کنید.":"عطر/لیست مربوط به موجودی فروش‌رفته را انتخاب کنید:",
                        found.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{InventoryReferenceName(v.PersianName,v.Perfume?.Name,v.EnglishName,v.DisplayCode)} — {(v.IsInventoryOffer?"موجودی":"لیست")} {v.DisplayCode}",InvAction("pick-sold:"+v.Id.ToString("N")))})
                        .Append(new[]{new TelegramInlineButton("↩️ موجودی‌ها",InvAction("menu"))}).ToArray(),ct);
                }
                else if(d.Step=="search")
                {
                    var found=await _db.SalesLists.AsNoTracking().Where(v=>!v.IsDeleted&&!v.IsInventoryOffer&&v.TelegramMessageId!=null&&(v.PublicCode==code||v.StablePublicCode==code||v.EnglishName.Contains(normalized)||v.PersianName.Contains(normalized)||v.Perfume.Name.Contains(normalized)))
                        .OrderByDescending(v=>v.OpenDate).Take(20).Select(v=>new{v.Id,v.PublicCode,v.StablePublicCode,v.EnglishName,v.PersianName,CatalogueName=v.Perfume.Name}).ToArrayAsync(ct);
                    await _sender.SendInlineKeyboardAsync(chat.ToString(),found.Length==0?"لیست پیدا نشد؛ کد/نام دیگری وارد کنید یا ثبت دستی را بزنید.":"لیست مرجع را انتخاب کنید؛ برای جست‌وجوی دیگر نام یا کد تازه بفرستید.",
                        found.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{InventoryReferenceName(v.PersianName,v.CatalogueName,v.EnglishName,v.StablePublicCode??v.PublicCode)} — {v.StablePublicCode??v.PublicCode}",InvAction("pick:"+v.Id.ToString("N")))}).Append(new[]{new TelegramInlineButton("✍️ ثبت دستی",InvAction("manual"))}).ToArray(),ct);
                }
                else
                {
                    var found=await _db.SalesLists.AsNoTracking().Where(v=>!v.IsDeleted&&(v.IsInventoryOffer||v.StoragePosition!=null)&&(v.InventoryCode==code||v.PublicCode==code||v.StablePublicCode==code||v.EnglishName.Contains(normalized)||v.PersianName.Contains(normalized)||
                        _db.SalesLists.Any(r=>r.Id==v.InventoryReferenceListId&&(r.PublicCode==code||r.StablePublicCode==code))))
                        .OrderByDescending(v=>v.CreatedAt).Take(20).ToArrayAsync(ct);
                    foreach(var list in found)await _sender.SendInlineKeyboardAsync(chat.ToString(),list.IsInventoryOffer?$"📍 کد موجودی: {list.DisplayCode}\n{list.EnglishName} — {list.TotalVolume} میل\nجایگاه: {list.StoragePosition??"جایگاه ثبت نشده"}\nوضعیت: {(list.Status==SalesListStatus.Open?"موجود":list.Status==SalesListStatus.Full?"رزرو شده":list.Status==SalesListStatus.Cancelled?"لغوشده":"فاکتورشده")}":$"📍 جایگاه موجودی فروش‌رفته\nکد لیست: {list.DisplayCode}\n{list.PersianName}\nجایگاه: {list.StoragePosition}",
                        [[new TelegramInlineButton("✏️ اصلاح جایگاه",InvAction((list.IsInventoryOffer?"move:":"move-sold:")+list.Id.ToString("N")))]],ct);
                    await InvPrompt(chat,found.Length==0?"موجودی پیدا نشد؛ نام یا کد دیگری بفرستید.":"برای جست‌وجوی دیگر نام یا کد را بفرستید.",ct);
                }
                return true;
            }
            if(d.Step=="sold-position")
            {
                await new ReadyInventoryService(_db).SetSoldPositionAsync(d.PositionList!.Value,text,message.From.Id,ct);
                InventoryDrafts.TryRemove(key,out _);await InvPrompt(chat,"✅ جایگاه موجودی فروش‌رفته ذخیره شد و در گزارش داخلی پست نمایش داده می‌شود.\nپستی در کانال منتشر نشد.",ct);return true;
            }
            if(d.Step=="move"){await new ReadyInventoryService(_db).SetPositionAsync(d.PositionList!.Value,text,message.From.Id,ct);InventoryDrafts.TryRemove(key,out _);await InvPrompt(chat,"✅ جایگاه فیزیکی به‌روز شد؛ قیمت و فاکتور تغییری نکرد.",ct);return true;}
            if(d.Step=="name"){if(text.Length is <1 or >200)throw new InvalidOperationException("نام عطر باید بین ۱ تا ۲۰۰ کاراکتر باشد.");d=d with{Name=text,Step=string.IsNullOrEmpty(d.Photo)?"photo":"position"};}
            else if(d.Step=="photo")
            {
                var photo=message.Photo?.OrderByDescending(v=>(long)v.Width*v.Height).FirstOrDefault();if(photo is null)throw new InvalidOperationException("عکس عطر را ارسال کنید.");
                var resume=d.PhotoReturnStep;d=d with{Photo=photo.FileId,Step=resume??(d.Price>0?"position":"price"),PhotoReturnStep=null};
                if(resume is "volume" or "price"){InventoryDrafts[key]=d;await ShowInventoryReferencePhotoAsync(chat,d,ct);return true;}
            }
            else if(d.Step=="price"){if(!decimal.TryParse(numeric,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||value<=0||value!=decimal.Round(value,2))throw new InvalidOperationException("قیمت مثبت هر میل را به تومان وارد کنید.");d=d with{Price=value,Step=d.Volume>0?"position":"volume"};}
            else if(d.Step=="volume")
            {
                if(!decimal.TryParse(numeric,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var volume)||volume is <0.5m or >1000||volume*2!=decimal.Truncate(volume*2))throw new InvalidOperationException("حجم را از ۰.۵ تا ۱۰۰۰ میل، با گام نیم‌میل وارد کنید؛ مثلاً ۱.۵ یا ۲ میل.");
                d=d with{Volume=volume,Step="bottle",Bottle=Guid.Empty};InventoryDrafts[key]=d;
                var bottles=await _db.Bottles.AsNoTracking().Where(v=>!v.IsDeleted&&v.IsActive&&v.Id!=ReadyInventoryService.OriginalBottleId&&v.VolumeMl>=volume).OrderBy(v=>v.VolumeMl).ThenBy(v=>v.SalePrice).Take(30).ToArrayAsync(ct);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),"شیشهٔ همین بستهٔ آماده را انتخاب کنید:",bottles.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{v.Name} — {v.VolumeMl} میل — {v.SalePrice:N0} تومان",InvAction("bottle:"+v.Id.ToString("N")))}).Prepend(new[]{new TelegramInlineButton("🧴 باتل اصلی رایگان",InvAction("original-bottle"))}).ToArray(),ct);return true;
            }
            else if(d.Step=="bottle-price"){if(!decimal.TryParse(numeric,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||value<0||value!=decimal.Round(value,2))throw new InvalidOperationException("مبلغ غیرمنفی شیشه را به تومان وارد کنید.");d=d with{BottlePrice=value,Step="position"};}
            else if(d.Step=="position")
            {
                if(string.IsNullOrWhiteSpace(text)||text.Length>150)throw new InvalidOperationException("جایگاه فیزیکی را حداکثر در ۱۵۰ کاراکتر وارد کنید.");
                d=d with{Position=text};InventoryDrafts[key]=d;await InvDescriptionOrPreview(chat,message.From.Id,d,ct);return true;
            }
            else if(d.Step=="description")
            {
                if(text.Length is <1 or >400)throw new InvalidOperationException("توضیحات را بین ۱ تا ۴۰۰ کاراکتر بنویسید یا دکمهٔ بدون توضیحات را بزنید.");
                await InvPreview(chat,message.From.Id,d with{Description=text,DescriptionCollected=true},ct);return true;
            }
            else {await InvPrompt(chat,"از دکمه‌های مرحلهٔ فعلی استفاده کنید.",ct);return true;}
            InventoryDrafts[key]=d;
            if(d.Step=="position")
            {if(d.Bottle!=Guid.Empty&&d.Position!=""&&d.Photo!=""&&d.Price>0){await InvDescriptionOrPreview(chat,message.From.Id,d,ct);return true;}await InvPositionPrompt(chat,ct);}
            else await InvPrompt(chat,d.Step=="photo"?"عکس عطر را بفرستید.":d.Step=="price"?"قیمت هر میل عطر را به تومان وارد کنید.":"حجم بستهٔ آماده را به میل وارد کنید.",ct);
        }
        catch(InvalidOperationException e){await InvPrompt(chat,e.Message,ct);}
        return true;
    }
    private async Task InvPreview(long chat,long user,InventoryDraft d,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(d.Photo)||d.Price<=0||d.Volume<0.5m||d.Bottle==Guid.Empty){await InvPrompt(chat,"عکس، قیمت، حجم یا شیشه کامل نیست؛ ثبت را دوباره آغاز کنید.",ct);return;}
        var token=Guid.NewGuid().ToString("N")[..8];InventoryDrafts[(chat,user)]=d with{Step="confirm",Token=token,Until=DateTime.UtcNow.AddMinutes(30)};
        await ReplyAsync(chat,$"👁 پیش‌نمایش موجودی؛ هنوز منتشر نشده است.\nجایگاه فقط داخلی: {d.Position}",ct);
        if(d.Bottle==ReadyInventoryService.OriginalBottleId)await ReplyAsync(chat,"🧴 باتل اصلی رایگان — هزینهٔ شیشه: ۰ تومان",ct);
        var preview=new SalesList{EnglishName=d.Name,PersianName=d.Name,PricePerMl=d.Price,TotalVolume=d.Volume,FixedBottlePrice=d.BottlePrice,Status=SalesListStatus.Open,InventoryPublicDescription=d.Description};
        await _sender.SendPhotoWithKeyboardAsync(chat.ToString(),d.Photo,ReadyInventoryCaption(preview).Replace("🔖 کد موجودی: <b>0</b>","🔖 کد چهاررقمی پس از ثبت تخصیص داده می‌شود."),
            [[new TelegramInlineButton("✅ ثبت و انتشار",InvAction("save:"+token))],
             [new TelegramInlineButton("✏️ قیمت هر میل",InvAction("edit:price")),new TelegramInlineButton("🖼 تغییر عکس",InvAction("edit:photo"))],
             [new TelegramInlineButton("✏️ حجم / شیشه",InvAction("edit:volume")),new TelegramInlineButton("📍 جایگاه",InvAction("edit:position"))],
             [new TelegramInlineButton("📝 اصلاح توضیحات",InvAction("edit:description"))],
             [new TelegramInlineButton("✏️ نام",InvAction("edit:name"))],[new TelegramInlineButton("❌ انصراف",InvAction("menu"))]],ct);
    }
}
