using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private static bool HasInventoryLocation(string? value)=>!string.IsNullOrWhiteSpace(value)&&
        value.Trim() is not("ثبت نشده" or "جایگاه ثبت نشده" or "نامشخص" or "-");
    private async Task<bool> TryHandleInventoryLocationCallbackAsync(TelegramCallbackQuery callback,string action,CancellationToken ct)
    {
        if(action!="register-position"&&!action.StartsWith("locpick:",StringComparison.Ordinal)&&
            !action.StartsWith("locok:",StringComparison.Ordinal)&&!action.StartsWith("locmove:",StringComparison.Ordinal)&&
            !action.StartsWith("locsave:",StringComparison.Ordinal))return false;
        var chat=callback.Message!.Chat.Id;var user=callback.From.Id;var key=(chat,user);
        if(action=="register-position")
        {
            ClearAdminWorkflowDrafts(chat,user);
            InventoryDrafts[key]=new(Guid.NewGuid(),"location-search",DateTime.UtcNow.AddMinutes(30));
            await InvPrompt(chat,"📍 ثبت جایگاه موجودی\nنام فارسی/انگلیسی یا کد موجودی عطر را وارد کنید.\nجایگاه فیزیکی فقط برای ادمین‌ها نمایش داده می‌شود.",ct);return true;
        }
        if(!InventoryDrafts.TryGetValue(key,out var draft)||draft.Until<DateTime.UtcNow)
            throw new InvalidOperationException("فرایند ثبت جایگاه منقضی شده؛ دوباره از منوی موجودی شروع کنید.");
        if(action.StartsWith("locpick:",StringComparison.Ordinal))
        {
            if(draft.Step!="location-search"||!Guid.TryParseExact(action[8..],"N",out var id)||!(draft.PositionChoices??[]).Contains(id))
                throw new InvalidOperationException("از نتایج آخرین جست‌وجوی همین فرم انتخاب کنید.");
            var list=await _db.SalesLists.AsNoTracking().Include(l=>l.Perfume).Include(l=>l.FixedBottle)
                .SingleOrDefaultAsync(l=>l.Id==id&&!l.IsDeleted&&l.IsInventoryOffer&&l.InventoryCode!=null&&l.Status!=SalesListStatus.Cancelled,ct)
                ??throw new InvalidOperationException("موجودی پیدا نشد؛ دوباره جست‌وجو کنید.");
            var token=Guid.NewGuid().ToString("N")[..12];
            InventoryDrafts[key]=draft with{Step="location-info",PositionList=id,Name=InventoryReferenceName(list.PersianName,list.Perfume?.Name,list.EnglishName,list.DisplayCode),ExpectedPosition=list.StoragePosition,Token=token};
            var text=$"📍 مشخصات موجودی\n{InventoryReferenceName(list.PersianName,list.Perfume?.Name,list.EnglishName,list.DisplayCode)}\nکد موجودی: {list.DisplayCode}\nحجم: {list.TotalVolume:0.#} میل\nشیشه: {list.FixedBottle?.Name??"ثبت نشده"}\nمبلغ بسته: {list.PricePerMl*list.TotalVolume+(list.FixedBottlePrice??0):N0} تومان\nجایگاه فعلی: {(HasInventoryLocation(list.StoragePosition)?list.StoragePosition:"ثبت نشده")}\n\nاین موجودی را تأیید می‌کنید؟";
            IReadOnlyCollection<TelegramInlineButton>[] buttons=[[new("✅ تأیید اطلاعات",InvAction("locok:"+token))],[new("❌ انصراف",InvAction("menu"))]];
            var shown=!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)&&
                (await _sender.SendPhotoWithKeyboardAsync(chat.ToString(),list.TelegramPhotoFileId,Html(text),buttons,ct)).IsSuccessful;
            if(!shown)await _sender.SendInlineKeyboardAsync(chat.ToString(),text,buttons,ct);
            return true;
        }
        var parts=action.Split(':');
        if(parts.Length!=2||draft.Token!=parts[1])throw new InvalidOperationException("از دکمه‌های پیش‌نمایش فعلی استفاده کنید.");
        if(parts[0]=="locok"&&draft.Step=="location-info")
        {
            if(HasInventoryLocation(draft.ExpectedPosition))
            {
                InventoryDrafts[key]=draft with{Step="location-move"};
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"این موجودی الان در جایگاه «{draft.ExpectedPosition}» است.\nمی‌خواهید به جایگاه جدید منتقل شود؟",
                    [[new("بله، انتقال به جایگاه جدید",InvAction("locmove:"+draft.Token))],[new("خیر؛ انصراف",InvAction("menu"))]],ct);return true;
            }
            InventoryDrafts[key]=draft with{Step="location-input"};await InvPositionPrompt(chat,ct);return true;
        }
        if(parts[0]=="locmove"&&draft.Step=="location-move")
        {InventoryDrafts[key]=draft with{Step="location-input"};await InvPositionPrompt(chat,ct);return true;}
        if(parts[0]=="locsave"&&draft.Step=="location-confirm"&&draft.PositionList.HasValue)
        {
            await new ReadyInventoryService(_db).SetPositionConfirmedAsync(draft.PositionList.Value,draft.ExpectedPosition,draft.Position,user,ct);
            InventoryDrafts.TryRemove(key,out _);
            await InvPrompt(chat,$"✅ جایگاه موجودی «{draft.Name}» ثبت شد.\n📍 جایگاه فیزیکی: {draft.Position}",ct);return true;
        }
        throw new InvalidOperationException("این مرحله قبلاً انجام شده؛ از دکمه‌های فعلی استفاده کنید.");
    }

    private async Task<bool> HandleInventoryLocationMessageAsync(TelegramMessage message,InventoryDraft draft,CancellationToken ct)
    {
        var chat=message.Chat.Id;var user=message.From!.Id;var key=(chat,user);var text=message.Text?.Trim()??"";
        if(draft.Step=="location-search")
        {
            if(text.Length is <1 or >150)throw new InvalidOperationException("نام یا کد موجودی معتبر وارد کنید.");
            int.TryParse(NormalizeNumber(text),out var code);var name=text.Replace('ي','ی').Replace('ك','ک');
            var found=await _db.SalesLists.AsNoTracking().Include(l=>l.Perfume).Where(l=>!l.IsDeleted&&l.IsInventoryOffer&&l.InventoryCode!=null&&l.Status!=SalesListStatus.Cancelled&&
                (l.InventoryCode==code||l.PersianName.Replace("ي","ی").Replace("ك","ک").Contains(name)||l.EnglishName.Contains(name)||l.Perfume.Name.Replace("ي","ی").Replace("ك","ک").Contains(name)))
                .OrderByDescending(l=>l.CreatedAt).ThenBy(l=>l.Id).Take(20).ToArrayAsync(ct);
            InventoryDrafts[key]=draft with{PositionChoices=found.Select(l=>l.Id).ToArray()};
            if(found.Length==0){await InvPrompt(chat,"موجودی پیدا نشد؛ نام یا کد دیگری وارد کنید.",ct);return true;}
            foreach(var list in found)
            {
                var title=InventoryReferenceName(list.PersianName,list.Perfume?.Name,list.EnglishName,list.DisplayCode);
                var caption=$"🧴 {title}\nکد موجودی: {list.DisplayCode}\nحجم: {list.TotalVolume:0.#} میل\nجایگاه فعلی: {(HasInventoryLocation(list.StoragePosition)?list.StoragePosition:"ثبت نشده")}";
                IReadOnlyCollection<TelegramInlineButton>[] choices=
                    [[new($"انتخاب {title} — {list.DisplayCode}",InvAction("locpick:"+list.Id.ToString("N")))],[new("❌ انصراف",InvAction("menu"))]];
                var pictured=!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)&&
                    (await _sender.SendPhotoWithKeyboardAsync(chat.ToString(),list.TelegramPhotoFileId,Html(caption),choices,ct)).IsSuccessful;
                if(!pictured)await _sender.SendInlineKeyboardAsync(chat.ToString(),caption+"\n📷 عکس در دسترس نیست.",choices,ct);
            }
            return true;
        }
        if(draft.Step=="location-input")
        {
            if(text.Length is <1 or >150)throw new InvalidOperationException("جایگاه فیزیکی را حداکثر در ۱۵۰ کاراکتر وارد کنید.");
            var token=Guid.NewGuid().ToString("N")[..12];InventoryDrafts[key]=draft with{Step="location-confirm",Position=text,Token=token};
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"ثبت جایگاه «{draft.Name}»\nجایگاه فعلی: {(HasInventoryLocation(draft.ExpectedPosition)?draft.ExpectedPosition:"ثبت نشده")}\nجایگاه جدید: {text}\n\nثبت شود؟",
                [[new("✅ ثبت جایگاه",InvAction("locsave:"+token))],[new("❌ انصراف",InvAction("menu"))]],ct);return true;
        }
        await InvPrompt(chat,"برای ادامه از دکمه‌های همین مرحله استفاده کنید.",ct);return true;
    }
}
