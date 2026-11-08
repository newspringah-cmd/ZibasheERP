using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private async Task ShowMarketingCopyChoiceAsync(TelegramMessage message,MarketingDraft draft,int code,Guid list,CancellationToken ct)
    {
        var token=Guid.NewGuid().ToString("N")[..8];
        MarketingDrafts[(message.Chat.Id,message.From!.Id)]=draft with {Code=code,SalesListId=list,SourceMessageId=message.MessageId,ChoiceToken=token,
            MessageText=null,CopyStage="copy-choice",ExpiresAt=DateTime.UtcNow.AddMinutes(20)};
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),$"لیست {code} انتخاب شد. متن تبلیغ را چطور آماده کنیم؟",
            [[new TelegramInlineButton("✨ متن توسط زیبا",$"invoiceadmin:marketing:copy-mode:{token}:ziba")],
             [new TelegramInlineButton("✍️ متن پیشنهادی ادمین",$"invoiceadmin:marketing:copy-mode:{token}:own")],
             [new TelegramInlineButton("↩️ انصراف","invoiceadmin:marketing:menu")]],ct);
    }
    private async Task HandleMarketingCopyCallbackAsync(TelegramCallbackQuery callback,string action,CancellationToken ct)
    {
        var key=(callback.Message!.Chat.Id,callback.From.Id);var parts=action.Split(':');
        if(!MarketingDrafts.TryGetValue(key,out var draft)||draft.ExpiresAt<=DateTime.UtcNow||draft.Code is null||draft.SalesListId is null||
            parts.Length<2||draft.ChoiceToken!=parts[1])
        {await ReplyAsync(key.Item1,"این انتخاب منقضی یا جایگزین شده است؛ کد عطر را دوباره وارد کنید.",ct);return;}
        if(parts[0]=="copy-mode"&&parts.Length==3&&draft.CopyStage=="copy-choice"&&parts[2] is "ziba" or "own")
        {
            if(parts[2]=="own")
            {
                MarketingDrafts[key]=draft with {CopyStage="copy-text",ChoiceToken=null};
                await ReplyAsync(key.Item1,$"متن پیشنهادی تبلیغ را بفرستید؛ حداکثر {(draft.Destination=="channel"?"۷۰۰":"۳۵۰۰")} نویسه.\nمتن شما بدون بازنویسی استفاده می‌شود؛ کد، موجودی و لینک خرید توسط ربات اضافه می‌شوند.\nبرای انصراف «لغو» را بفرستید.",ct);return;
            }
            await ContinueMarketingCopyAsync(key.Item1,key.Item2,draft with {MessageText=null},ct);return;
        }
        if(parts[0]=="copy-send"&&parts.Length==2&&draft.CopyStage=="copy-preview"&&draft.Destination=="group"&&!string.IsNullOrWhiteSpace(draft.MessageText))
        {await ContinueMarketingCopyAsync(key.Item1,key.Item2,draft,ct);return;}
        await ReplyAsync(key.Item1,"این انتخاب منقضی یا جایگزین شده است؛ کد عطر را دوباره وارد کنید.",ct);
    }
    private async Task PreviewMarketingCopyAsync(TelegramMessage message,MarketingDraft draft,CancellationToken ct)
    {
        var text=message.Text;var limit=draft.Destination=="channel"?700:3500;
        if(string.IsNullOrWhiteSpace(text)||text.Length>limit)
        {await ReplyAsync(message.Chat.Id,$"متن تبلیغ را به‌صورت پیام متنی، حداکثر {limit} نویسه بفرستید. عکس‌ها طبق روال توسط ربات انتخاب می‌شوند.",ct);return;}
        var token=Guid.NewGuid().ToString("N")[..8];
        var next=draft with {MessageText=text,SourceMessageId=message.MessageId,ChoiceToken=token,CopyStage="copy-preview",ExpiresAt=DateTime.UtcNow.AddMinutes(20)};
        MarketingDrafts[(message.Chat.Id,message.From!.Id)]=next;
        await ReplyAsync(message.Chat.Id,"👁 متن پیشنهادی شما؛ هنوز تبلیغ ارسال نشده است. کد، موجودی و لینک خرید همراه آن قرار می‌گیرند. برای اصلاح، متن جدید بفرستید.",ct);
        await ReplyAsync(message.Chat.Id,text,ct);
        if(draft.Destination=="channel"){await ContinueMarketingCopyAsync(message.Chat.Id,message.From.Id,next,ct);return;}
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),$"تبلیغ لیست {draft.Code} با همین متن در گروه ارسال شود؟",
            [[new TelegramInlineButton("✅ تأیید و ارسال تبلیغ",$"invoiceadmin:marketing:copy-send:{token}")],
             [new TelegramInlineButton("↩️ انصراف","invoiceadmin:marketing:menu")]],ct);
    }
    private async Task ContinueMarketingCopyAsync(long chat,long user,MarketingDraft draft,CancellationToken ct)
    {
        var available=await _db.SalesLists.AsNoTracking().AnyAsync(v=>v.Id==draft.SalesListId&&!v.IsDeleted&&!v.IsInventoryOffer&&v.Status==SalesListStatus.Open&&v.TotalVolume>v.ReservedVolume&&
            (v.StablePublicCode??v.PublicCode)==draft.Code&&v.TelegramMessageId!=null&&v.TelegramChannelId!=null&&v.TelegramPhotoFileId!=null,ct);
        if(!MarketingDrafts.TryGetValue((chat,user),out var latest)||latest.SourceMessageId!=draft.SourceMessageId||latest.SalesListId!=draft.SalesListId||latest.MessageText!=draft.MessageText)
            return;
        if(!available){MarketingDrafts.TryRemove((chat,user),out _);await ReplyAsync(chat,"این لیست دیگر برای تبلیغ باز نیست؛ کد دیگری وارد کنید.",ct);return;}
        if(draft.Destination=="channel")
        {
            var token=Guid.NewGuid().ToString("N")[..8];MarketingDrafts[(chat,user)]=draft with{ChoiceToken=token,CopyStage="channel-mode"};
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"📢 تبلیغ کانال — لیست {draft.Code}\n\nتبلیغ‌های قبلی بمانند یا حذف شوند؟\nموجودی هر تبلیغ جداگانه به‌روز می‌شود و با تکمیل لیست، همان تبلیغ حذف می‌شود.",
                [[new TelegramInlineButton("➕ قبلی‌ها بمانند؛ این هم ارسال شود",$"invoiceadmin:marketing:channel-mode:{token}:append")],
                 [new TelegramInlineButton("🔄 قبلی‌ها حذف شوند؛ این ارسال شود",$"invoiceadmin:marketing:channel-mode:{token}:replace")],
                 [new TelegramInlineButton("↩️ انصراف","invoiceadmin:marketing:menu")]],ct);return;
        }
        await QueueMarketingPromotionAsync(chat,user,draft.SourceMessageId,draft.Code!.Value,draft.SalesListId!.Value,"group","replace",ct,draft.MessageText);
        if(MarketingDrafts.TryGetValue((chat,user),out var current)&&current.SourceMessageId==draft.SourceMessageId)MarketingDrafts.TryRemove((chat,user),out _);
    }
}
