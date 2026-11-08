using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private sealed record InventoryBuyer(string Identity,string UserId,string? Username,Guid? CustomerId,string Destination);

    private async Task<InventoryBuyer> FindInventoryBuyerAsync(string input,CancellationToken ct)
    {
        var text=NormalizeNumber(input.Trim());
        string? username=null;
        if(text.StartsWith("https://t.me/",StringComparison.OrdinalIgnoreCase))text=text[13..];
        else if(text.StartsWith("t.me/",StringComparison.OrdinalIgnoreCase))text=text[5..];
        var numeric=text.All(char.IsAsciiDigit)&&long.TryParse(text,out var numericId)&&numericId>0;
        if(numeric)text=long.Parse(text,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        else
        {
            username=text.TrimStart('@').ToLowerInvariant();
            if(username.Length is <5 or >32||!char.IsAsciiLetter(username[0])||username.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))
                throw new InvalidOperationException("آیدی معتبر به صورت @username یا شناسهٔ عددی تلگرام وارد کنید.");
        }
        var matches=await _db.Customers.AsNoTracking().Include(v=>v.TelegramGroup)
            .Where(v=>!v.IsDeleted&&(numeric?v.TelegramId==text:
                v.Username!=null&&(v.Username.ToLower()==username||v.Username.ToLower()=="@"+username)))
            .Take(2).ToArrayAsync(ct);
        if(matches.Length>1)throw new InvalidOperationException("این آیدی به چند مشتری مرتبط است؛ شناسهٔ عددی دقیق مشتری را وارد کنید.");
        var customer=matches.SingleOrDefault();
        if(numeric)username=string.IsNullOrWhiteSpace(customer?.Username)?null:customer.Username.Trim().TrimStart('@');
        var userId=numeric?text:long.TryParse(customer?.TelegramId,out var knownId)&&knownId>0
            ?knownId.ToString(CultureInfo.InvariantCulture):"admin-username:"+username;
        var destination=customer?.TelegramGroup is { IsActive:true,IsDeleted:false }
            ?"فاکتور به گروه حسابداری متصل همین مشتری ارسال می‌شود."
            :"گروه حسابداری مشتری متصل نیست؛ فاکتور در بخش به‌مقصدنرسیده‌ها می‌ماند و پس از اتصال ارسال می‌شود.";
        return new(numeric?text:"@"+username,userId,username,customer?.Id,destination);
    }

    private async Task<bool> HandleInventoryPurchaseMessageAsync(TelegramMessage message,InventoryDraft draft,CancellationToken ct)
    {
        var key=(message.Chat.Id,message.From!.Id);var chat=message.Chat.Id;
        if(draft.Step=="buy-code")
        {
            if(!int.TryParse(NormalizeNumber(message.Text??""),out var code)||code is <1000 or >9999)
                throw new InvalidOperationException("کد چهاررقمی موجودی را وارد کنید؛ کد لیست عطر نیست.");
            var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>!v.IsDeleted&&v.IsInventoryOffer&&v.InventoryCode==code,ct)
                ??throw new InvalidOperationException("این کد موجودی پیدا نشد.");
            if(list.Status!=SalesListStatus.Open||list.RemainingVolume!=list.TotalVolume)
                throw new InvalidOperationException("این موجودی قبلاً رزرو یا فاکتور شده است؛ کد موجودی دیگری وارد کنید.");
            InventoryDrafts[key]=draft with{Step="buy-identity",PurchaseList=list.Id};
            await InvPrompt(chat,$"موجودی {list.DisplayCode} — {list.PersianName}\nحجم بسته: {list.TotalVolume:0.#} میل\nآیدی درخواست‌دهنده را به صورت @username یا شناسهٔ عددی تلگرام وارد کنید.",ct);
            return true;
        }
        if(draft.Step=="buy-identity")
        {
            var buyer=await FindInventoryBuyerAsync(message.Text??"",ct);
            var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==draft.PurchaseList&&!v.IsDeleted&&v.IsInventoryOffer&&v.InventoryCode!=null,ct)
                ??throw new InvalidOperationException("موجودی پیدا نشد.");
            if(!list.CanReserveVolume(list.TotalVolume)||list.FixedBottleId is null||list.FixedBottlePrice is null)
                throw new InvalidOperationException("این موجودی دیگر قابل ثبت نیست.");
            var quote=await FlashSalePricing.QuoteAsync(_db,list,DateTime.UtcNow,ct);
            var next=draft with{Step="buy-confirm",BuyerIdentity=buyer.Identity,BuyerUserId=buyer.UserId,BuyerUsername=buyer.Username,
                BuyerCustomerId=buyer.CustomerId,Name=list.PersianName,Volume=list.TotalVolume,Price=quote.PricePerMl,
                PromotionId=quote.PromotionId,Bottle=list.FixedBottleId.Value,BottlePrice=list.FixedBottlePrice.Value,Token=Guid.NewGuid().ToString("N")};
            InventoryDrafts[key]=next;
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"🛍 ثبت خرید موجودی {list.DisplayCode}\n{next.Name}\nمشتری: {buyer.Identity}\nحجم کامل بسته: {next.Volume:0.#} میل\nمبلغ عطر: {next.Volume*next.Price:N0} تومان\nهزینهٔ شیشه: {next.BottlePrice:N0} تومان\nجمع فاکتور: {next.Volume*next.Price+next.BottlePrice:N0} تومان\n\n{buyer.Destination}\nبا تأیید، خرید برای همین مشتری ثبت و فاکتور صادر می‌شود.",
                [[new TelegramInlineButton("✅ ثبت و صدور فاکتور",InvAction("buy-save:"+next.Token))],
                 [new TelegramInlineButton("✏️ اصلاح آیدی مشتری",InvAction("buy-identity"))],
                 [new TelegramInlineButton("❌ انصراف",InvAction("menu"))]],ct);
            return true;
        }
        await InvPrompt(chat,"پیش‌نمایش خرید آماده است؛ دکمهٔ ثبت و صدور فاکتور یا اصلاح آیدی مشتری را بزنید.",ct);
        return true;
    }

    private async Task<bool> TryHandleInventoryPurchaseCallbackAsync(TelegramCallbackQuery callback,string action,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var key=(chat,callback.From.Id);
        if(action=="buy")
        {
            ClearAdminWorkflowDrafts(chat,callback.From.Id);
            InventoryDrafts[key]=new(Guid.NewGuid(),"buy-code",DateTime.UtcNow.AddMinutes(30));
            await InvPrompt(chat,"🛍 ثبت خرید موجودی برای مشتری\nکد چهاررقمی موجودی را وارد کنید.",ct);return true;
        }
        if(action=="buy-identity")
        {
            if(!InventoryDrafts.TryGetValue(key,out var edit)||edit.Step!="buy-confirm"||edit.Until<DateTime.UtcNow)
                throw new InvalidOperationException("پیش‌نمایش منقضی شده است؛ ثبت خرید را دوباره شروع کنید.");
            var existing=await _db.SalesListRequests.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==edit.Id,ct);
            if(existing is not null&&existing.Status!=SalesListRequestStatus.PendingConfirmation)
                throw new InvalidOperationException("خرید قبلاً برای مشتری ثبت شده است؛ برای ادامه همان تأیید را بزنید یا از موجودی‌های درخواست‌شده پیگیری کنید.");
            if(existing is not null)await _salesListRequestRepository.CancelAsync(existing.Id,existing.TelegramUserId,ct);
            InventoryDrafts[key]=edit with{Id=Guid.NewGuid(),Step="buy-identity",Token=null};
            await InvPrompt(chat,"آیدی صحیح مشتری را به صورت @username یا شناسهٔ عددی تلگرام وارد کنید.",ct);return true;
        }
        if(!action.StartsWith("buy-save:",StringComparison.Ordinal))return false;
        var gate=ReservationStartLocks[(int)((uint)HashCode.Combine(chat,callback.From.Id)%(uint)ReservationStartLocks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            if(!InventoryDrafts.TryGetValue(key,out var draft)||draft.Step!="buy-confirm"||draft.Token!=action[9..]||draft.Until<DateTime.UtcNow)
                throw new InvalidOperationException("این پیش‌نمایش منقضی یا قبلاً ثبت شده است؛ از ثبت دوباره خودداری کنید.");
            var buyer=await FindInventoryBuyerAsync(draft.BuyerIdentity,ct);
            if((draft.BuyerCustomerId.HasValue&&buyer.CustomerId!=draft.BuyerCustomerId)||buyer.UserId!=draft.BuyerUserId)
                throw new InvalidOperationException("اطلاعات مشتری تغییر کرده است؛ آیدی را دوباره بررسی کنید.");
            var request=await _db.SalesListRequests.SingleOrDefaultAsync(v=>v.Id==draft.Id,ct);
            if(request is not null&&(request.IsDeleted||!request.CreatedByAdmin||request.SalesListId!=draft.PurchaseList||
                request.TelegramUserId!=draft.BuyerUserId||!string.Equals(request.TelegramUsername,draft.BuyerUsername,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("درخواست ثبت‌شده با مشتری پیش‌نمایش یکسان نیست؛ از موجودی‌های درخواست‌شده پیگیری کنید.");
            if(request is null)
            {
                request=new SalesListRequest{Id=draft.Id,CreatedAt=DateTime.UtcNow,SalesListId=draft.PurchaseList!.Value,
                    TelegramUserId=buyer.UserId,TelegramUsername=buyer.Username,VolumeMl=draft.Volume,PerfumePricePerMl=draft.Price,
                    FlashSalePromotionId=draft.PromotionId,BottleId=draft.Bottle,BottlePrice=draft.BottlePrice,CreatedByAdmin=true,
                    Kind=SalesListRequestKind.CurrentBottle,Status=SalesListRequestStatus.PendingConfirmation,
                    ExpiresAt=DateTime.UtcNow.AddMinutes(10),ExternalReference="admin-inventory-purchase:"+draft.Id.ToString("N")};
                await _salesListRequestRepository.AddAsync(request,ct);await _salesListRequestRepository.SaveChangesAsync(ct);
            }
            if(request.Status==SalesListRequestStatus.PendingConfirmation)
                await _salesListRequestRepository.ConfirmCurrentBottleAsync(request.Id,request.TelegramUserId,ct);
            else if(request.Status is not(SalesListRequestStatus.Confirmed or SalesListRequestStatus.QueuedForInvoice or SalesListRequestStatus.Invoiced))
                throw new InvalidOperationException("این درخواست خرید دیگر فعال نیست؛ ثبت خرید را دوباره شروع کنید.");
            var result=await _invoiceIssuanceService.IssueCompletedListsAsync([request.SalesListId],callback.From.Id.ToString(),ct);
            InventoryDrafts.TryRemove(key,out _);
            await InvPrompt(chat,$"✅ خرید موجودی برای {draft.BuyerIdentity} ثبت و فاکتور صادر شد.\n{string.Join("، ",result.InvoiceNumbers)}\n{buyer.Destination}",ct);
            return true;
        }
        finally{gate.Release();}
    }
}
