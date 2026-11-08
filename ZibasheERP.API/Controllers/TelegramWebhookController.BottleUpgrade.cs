using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private sealed record BottleUpgradeDraft(string Step,DateTime Until,Guid List=default,Guid Request=default,
        Guid Target=default,decimal Price=0,byte[]? Version=null,string? Token=null);
    private static readonly ConcurrentDictionary<(long Chat,long User),BottleUpgradeDraft> BottleUpgradeDrafts=new();
    private const string BottleUpgradePrefix="invoiceadmin:bup:";
    private Task BottleUpgradePrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new TelegramInlineButton("❌ انصراف",BottleUpgradePrefix+"cancel")]],ct);
    private async Task HandleBottleUpgradeCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var key=(chat,callback.From.Id);
        if(callback.Message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"تغییر شیشه فقط برای ادمین‌های مجاز در گفت‌وگوی خصوصی ربات است.",ct,true);return;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        var action=callback.Data![BottleUpgradePrefix.Length..];
        if(action=="cancel"){BottleUpgradeDrafts.TryRemove(key,out _);await BottleUpgradePrompt(chat,"تغییر شیشه لغو شد.",ct);return;}
        if(action=="start")
        {ClearAdminWorkflowDrafts(chat,callback.From.Id);BottleUpgradeDrafts[key]=new("list",DateTime.UtcNow.AddMinutes(15));await BottleUpgradePrompt(chat,"کد لیست یا نام عطر را وارد کنید.\nشیشهٔ آیتم‌های لیست را قبل از صدور فاکتور تغییر دهید؛ حجم عطر ثابت می‌ماند.",ct);return;}
        try
        {
            if(!BottleUpgradeDrafts.TryGetValue(key,out var d)||d.Until<DateTime.UtcNow)throw new InvalidOperationException("فرایند منقضی شده؛ از منوی آیتم‌ها دوباره شروع کنید.");
            var parts=action.Split(':');
            if(parts.Length==2&&parts[0]=="list"&&d.Step=="list"&&Guid.TryParseExact(parts[1],"N",out var listId))
            {
                var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==listId&&!v.IsDeleted&&!v.IsInventoryOffer&&v.Status!=SalesListStatus.Invoiced&&v.Status!=SalesListStatus.Cancelled,ct)
                    ??throw new InvalidOperationException("لیست قابل ویرایش نیست.");
                BottleUpgradeDrafts[key]=d with{Step="identity",List=list.Id};
                await BottleUpgradePrompt(chat,$"لیست {list.DisplayCode} — {list.PersianName}\nآیدی مشتری را وارد کنید؛ آیتم‌های خرید شخصی و هدیهٔ دریافتی او نمایش داده می‌شود.",ct);return;
            }
            if(parts.Length==2&&parts[0]=="item"&&d.Step=="item"&&Guid.TryParseExact(parts[1],"N",out var requestId))
            {
                var r=await new SmallBottleUpgradeService(_db).PreviewAsync(requestId,ct);
                if(r.SalesListId!=d.List)throw new InvalidOperationException("آیتم مربوط به این لیست نیست.");
                var bottles=await _db.Bottles.AsNoTracking().Where(v=>!v.IsDeleted&&v.IsActive&&(v.Type==BottleType.Normal||v.Type==BottleType.Fancy)&&v.VolumeMl>=r.VolumeMl&&v.Id!=r.BottleId&&v.SalePrice>0).OrderBy(v=>v.VolumeMl).ThenBy(v=>v.Name).ToArrayAsync(ct);
                BottleUpgradeDrafts[key]=d with{Step="bottle",Request=r.Id,Version=r.RowVersion};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"{DisplayUser(r)} — {r.VolumeMl:0} میل{(r.IsGift?$"، هدیه برای @{r.GiftRecipientTelegramUsername??r.GiftRecipientTelegramUserId}":"")}\nشیشهٔ مقصد را انتخاب کنید؛ مقدار عطر تغییر نمی‌کند.",
                    bottles.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{(v.Type==BottleType.Fancy?"فانتزی":"نرمال")} {v.VolumeMl} میل — {v.Name} — {(r.IsComplimentaryBottle?0:v.SalePrice):N0} تومان",BottleUpgradePrefix+"target:"+v.Id.ToString("N"))})
                    .Append(new[]{new TelegramInlineButton("❌ انصراف",BottleUpgradePrefix+"cancel")}).ToArray(),ct);return;
            }
            if(parts.Length==2&&parts[0]=="target"&&d.Step=="bottle"&&Guid.TryParseExact(parts[1],"N",out var bottleId))
            {
                var r=await new SmallBottleUpgradeService(_db).PreviewAsync(d.Request,ct);
                if(d.Version is null||!r.RowVersion.SequenceEqual(d.Version))throw new InvalidOperationException("آیتم تغییر کرده؛ دوباره شروع کنید.");
                var b=await _db.Bottles.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==bottleId&&!v.IsDeleted&&v.IsActive&&(v.Type==BottleType.Normal||v.Type==BottleType.Fancy)&&v.VolumeMl>=r.VolumeMl&&v.Id!=r.BottleId&&v.SalePrice>0,ct)
                    ??throw new InvalidOperationException("شیشهٔ فعال با ظرفیت کافی برای مقدار عطر پیدا نشد.");
                var token=Guid.NewGuid().ToString("N")[..12];BottleUpgradeDrafts[key]=d with{Step="confirm",Target=b.Id,Price=b.SalePrice,Token=token};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"👁 تأیید تغییر شیشه\nلیست {r.SalesList.DisplayCode} — {r.SalesList.PersianName}\n{DisplayUser(r)}{(r.IsGift?$" — هدیه برای @{r.GiftRecipientTelegramUsername??r.GiftRecipientTelegramUserId}":"")}\nمقدار عطر: {r.VolumeMl:0} میل (بدون تغییر)\nشیشهٔ فعلی: {r.Bottle?.Name??"ثبت نشده"}\nشیشهٔ جدید: {(b.Type==BottleType.Fancy?"فانتزی":"نرمال")} {b.VolumeMl} میل — {b.Name}\nمبلغ شیشه در فاکتور: {(r.IsComplimentaryBottle?0:b.SalePrice):N0} تومان\n\nفقط شیشهٔ همین آیتم تغییر می‌کند؛ حجم و اطلاعات هدیه حفظ می‌شود.",
                    [[new TelegramInlineButton("✅ تأیید تغییر شیشه",BottleUpgradePrefix+"save:"+token)],[new TelegramInlineButton("❌ انصراف",BottleUpgradePrefix+"cancel")]],ct);return;
            }
            if(parts.Length==2&&parts[0]=="save"&&d.Step=="confirm"&&parts[1]==d.Token&&d.Version is not null)
            {
                await new SmallBottleUpgradeService(_db).UpgradeAsync(d.Request,d.Version,d.Target,d.Price,callback.From.Id,ct);
                BottleUpgradeDrafts.TryRemove(key,out _);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),"✅ نوع شیشه تغییر کرد؛ حجم عطر ثابت ماند و مبلغ شیشهٔ جدید موقع فاکتور محاسبه می‌شود. به‌روزرسانی پست لیست در صف است.",
                    [[new TelegramInlineButton("↩️ آیتم‌ها","invoiceadmin:menu:items")]],ct);return;
            }
            throw new InvalidOperationException("این دکمه مربوط به مرحلهٔ فعلی نیست؛ دوباره شروع کنید.");
        }
        catch(InvalidOperationException e){await BottleUpgradePrompt(chat,e.Message,ct);}
    }
    private async Task<bool> TryHandleBottleUpgradeMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!BottleUpgradeDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var d))return false;
        var key=(message.Chat.Id,message.From.Id);var chat=message.Chat.Id;var text=message.Text?.Trim()??"";
        if(message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct))
        {BottleUpgradeDrafts.TryRemove(key,out _);return false;}
        if(text.StartsWith('/')||d.Until<DateTime.UtcNow){BottleUpgradeDrafts.TryRemove(key,out _);return false;}
        if(text.Length is <1 or >150){await BottleUpgradePrompt(chat,"نام، کد یا آیدی معتبر وارد کنید.",ct);return true;}
        if(d.Step=="list")
        {
            int.TryParse(NormalizeNumber(text),out var code);var name=text.Replace('ي','ی').Replace('ك','ک');
            var lists=await _db.SalesLists.AsNoTracking().Where(v=>!v.IsDeleted&&!v.IsInventoryOffer&&v.Status!=SalesListStatus.Invoiced&&v.Status!=SalesListStatus.Cancelled&&v.Status!=SalesListStatus.QueuedForInvoice&&
                (v.PublicCode==code||v.StablePublicCode==code||v.PersianName.Contains(name)||v.EnglishName.Contains(name))).OrderByDescending(v=>v.OpenDate).Take(20).ToArrayAsync(ct);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),lists.Length==0?"لیست قابل ویرایش پیدا نشد؛ نام یا کد دیگری وارد کنید.":"لیست را انتخاب کنید:",
                lists.Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{v.DisplayCode} — {InventoryReferenceName(v.PersianName,null,v.EnglishName,v.DisplayCode)}",BottleUpgradePrefix+"list:"+v.Id.ToString("N"))})
                .Append(new[]{new TelegramInlineButton("❌ انصراف",BottleUpgradePrefix+"cancel")}).ToArray(),ct);return true;
        }
        if(d.Step=="identity")
        {
            var username=NormalizeAdminRequestUsername(text);var numeric=NormalizeNumber(text);
            var rows=await _db.SalesListRequests.AsNoTracking().Include(v=>v.SalesList).Include(v=>v.Bottle)
                .Where(v=>v.SalesListId==d.List&&!v.IsDeleted&&v.Status==SalesListRequestStatus.Confirmed&&
                    ((username!=null&&(v.TelegramUsername==username||v.GiftRecipientTelegramUsername==username))||v.TelegramUserId==numeric||v.GiftRecipientTelegramUserId==numeric)).ToArrayAsync(ct);
            rows=rows.Where(SmallBottleUpgradeService.CanUpgrade).ToArray();
            if(rows.Length==0){await BottleUpgradePrompt(chat,"آیتم فاکتورنشدهٔ قابل تغییر برای این مشتری پیدا نشد.",ct);return true;}
            BottleUpgradeDrafts[key]=d with{Step="item"};
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"آیتمی که شیشه‌اش تغییر می‌کند را انتخاب کنید؛ خرید شخصی و هدیه جدا هستند:",
                rows.OrderBy(v=>v.CreatedAt).Take(40).Select(v=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"{DisplayUser(v)}{(v.IsGift?$" ← @{v.GiftRecipientTelegramUsername??v.GiftRecipientTelegramUserId} 🎁":"")} — {v.VolumeMl:0} میل",BottleUpgradePrefix+"item:"+v.Id.ToString("N"))})
                .Append(new[]{new TelegramInlineButton("❌ انصراف",BottleUpgradePrefix+"cancel")}).ToArray(),ct);return true;
        }
        await BottleUpgradePrompt(chat,"از دکمه‌های مرحلهٔ فعلی استفاده کنید.",ct);return true;
    }
}
