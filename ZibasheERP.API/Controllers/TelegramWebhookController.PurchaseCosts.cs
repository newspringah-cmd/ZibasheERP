using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string PurchaseCostPrefix="purchase-cost:";
    private sealed record PurchaseCostDraft(string Step,DateTime Until,Guid? ListId=null,string? Variant=null,
        decimal? Amount=null,Guid? Token=null,Guid[]? Choices=null,string? Query=null,int Page=0);
    private static readonly ConcurrentDictionary<(long Chat,long User),PurchaseCostDraft> PurchaseCostDrafts=new();
    private Task PurchaseCostPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new("❌ انصراف",PurchaseCostPrefix+"cancel")]],ct);
    private static bool TryPurchaseAmount(string text,out decimal value)
    {
        text=NormalizeNumber(text.Trim()).Replace("٬","").Replace(",","").Replace("٫",".");
        return decimal.TryParse(text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)&&
            value>0&&value<=1000000000m&&decimal.Round(value,4)==value;
    }
    private static string CostDate(DateTime utc)=>FormatShippingPersianDate(utc)+" "+
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(utc,DateTimeKind.Utc),"Asia/Tehran").ToString("HH:mm")+" — تهران";

    private static string PurchaseCostCaption(PerfumePurchaseCost cost)=>
        (cost.AmountAed.HasValue?$"💵 خرید باتل: {cost.AmountAed:0.####} درهم\n":"")+
        (cost.Rate.HasValue?$"نرخ هر درهم: {cost.Rate:0.####} تومان\nمعتبر از: {CostDate(cost.RateSince!.Value)}\n":"")+
        (cost.CostToman.HasValue?$"هزینه خرید{(cost.Estimate?" برآوردی با نرخ فعلی":" با نرخ زمان تکمیل")}: {cost.CostToman:N0} تومان":$"⚠️ {cost.Missing}");

    private async Task<bool> TryHandlePurchaseCostCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Data?.StartsWith(PurchaseCostPrefix,StringComparison.Ordinal)!=true)return false;
        var actor=callback.From.Id;var message=callback.Message;
        if(message is null||message.Chat.Type!="private"||message.Chat.Id!=actor||!IsPrimaryOwner(actor))
        {await _sender.AnswerCallbackAsync(callback.Id,"این گزینه فقط برای مدیر اصلی در خصوصی ربات است.",ct,true);return true;}
        var action=callback.Data[PurchaseCostPrefix.Length..];var key=(actor,actor);
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        try
        {
            if(action=="cancel")
            {PurchaseCostDrafts.TryRemove(key,out _);await _sender.SendInlineKeyboardAsync(actor.ToString(),"عملیات ثبت هزینه خرید بسته شد.",[[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);return true;}
            if(action is "price" or "rate")
            {
                ClearAdminWorkflowDrafts(actor,actor);PurchaseCostDrafts[key]=new(action=="price"?"search":"rate",DateTime.UtcNow.AddMinutes(20));
                if(action=="price")await PurchaseCostPrompt(actor,"🧴 ثبت قیمت خرید درهمی عطر\nکد لیست یا نام فارسی/انگلیسی عطر را بفرستید.\nقیمت خرید یک باتل کامل با حجم همان لیست ثبت می‌شود؛ این قیمت فقط برای گزارش مدیر است.",ct);
                else
                {
                    var current=await _db.DirhamExchangeRates.AsNoTracking().Where(r=>!r.IsDeleted&&r.EffectiveAtUtc<=DateTime.UtcNow).OrderByDescending(r=>r.EffectiveAtUtc).FirstOrDefaultAsync(ct);
                    await PurchaseCostPrompt(actor,"💱 ثبت نرخ درهم\n"+(current is null?"هنوز نرخی ثبت نشده.":$"نرخ فعلی هر درهم: {current.TomanPerDirham:N4} تومان\nمعتبر از: {CostDate(current.EffectiveAtUtc)}")+
                        "\nنرخ هر ۱ درهم را به تومان وارد کنید.\nنرخ جدید از لحظهٔ تأیید تا ثبت نرخ بعدی معتبر است و نرخ روزهای گذشته را تغییر نمی‌دهد.",ct);
                }
                return true;
            }
            if(!PurchaseCostDrafts.TryGetValue(key,out var draft)||draft.Until<DateTime.UtcNow)throw new InvalidOperationException("مهلت این مرحله تمام شده؛ از منوی مدیر دوباره شروع کنید.");
            if(action is "next" or "prev"&&draft.Step=="search"&&draft.Query is not null)
            {await SearchPurchaseCostAsync(actor,draft with{Page=Math.Max(0,draft.Page+(action=="next"?1:-1))},ct);return true;}
            if(action.StartsWith("pick:")&&Guid.TryParseExact(action[5..],"N",out var selected)&&draft.Step=="search"&&draft.Choices?.Contains(selected)==true)
            {
                var list=await _db.SalesLists.AsNoTracking().SingleOrDefaultAsync(l=>l.Id==selected&&!l.IsDeleted&&!l.IsInventoryOffer&&l.TotalVolume>0,ct)??throw new InvalidOperationException("لیست عطر در دسترس نیست.");
                PurchaseCostDrafts[key]=new("amount",DateTime.UtcNow.AddMinutes(20),list.Id,PerfumePurchaseCostService.VariantKey(list));
                var price=await CurrentPurchasePriceAsync(list,ct);
                await PurchaseCostPrompt(actor,$"🧴 {list.PersianName}\n{list.EnglishName}\nکد: {list.DisplayCode}\nحجم باتل: {list.TotalVolume:0.#} میل\n"+
                    (price.HasValue?$"قیمت خرید ثبت‌شده: {price:0.####} درهم\n":"قیمت خرید ثبت نشده.\n")+
                    "قیمت خرید همین باتل کامل را به درهم وارد کنید.\nاین کار قیمت فروش مشتری را تغییر نمی‌دهد.",ct);return true;
            }
            if(action.StartsWith("save:")&&Guid.TryParseExact(action[5..],"N",out var token)&&draft.Token==token&&draft.Amount.HasValue)
            {
                var service=new PerfumePurchaseCostService(_db);
                if(draft.Step=="rate-confirm")await service.RegisterRateAsync(token,draft.Amount.Value,actor,ct);
                else if(draft.Step=="price-confirm"&&draft.ListId.HasValue&&draft.Variant is not null)
                    await service.RegisterPriceAsync(token,draft.ListId.Value,draft.Variant,draft.Amount.Value,actor,ct);
                else throw new InvalidOperationException("مرحلهٔ تأیید معتبر نیست.");
                ((ICollection<KeyValuePair<(long Chat,long User),PurchaseCostDraft>>)PurchaseCostDrafts).Remove(new(key,draft));
                await _sender.SendInlineKeyboardAsync(actor.ToString(),draft.Step=="rate-confirm"?
                    $"✅ نرخ هر درهم {draft.Amount:0.####} تومان ثبت شد.\nتا ثبت نرخ بعدی معتبر است؛ محاسبات لیست‌های تکمیل‌شدهٔ قبلی با نرخ همان زمان باقی می‌مانند.":
                    $"✅ قیمت خرید باتل: {draft.Amount:0.####} درهم ثبت شد.\nهزینهٔ خرید با نرخ معتبر هنگام تکمیل لیست محاسبه می‌شود. این اطلاعات فقط در گزارش مدیر است.",
                    [[new("🧴 ثبت قیمت عطر دیگر",PurchaseCostPrefix+"price")],[new("💱 ثبت نرخ درهم",PurchaseCostPrefix+"rate")],[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);return true;
            }
            throw new InvalidOperationException("از دکمه‌های مرحلهٔ فعلی استفاده کنید یا دوباره از منو شروع کنید.");
        }
        catch(InvalidOperationException e){await PurchaseCostPrompt(actor,e.Message,ct);}
        return true;
    }

    private async Task<bool> TryHandlePurchaseCostMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!PurchaseCostDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var draft))return false;
        var key=(message.Chat.Id,message.From.Id);var text=message.Text?.Trim()??"";
        if(message.Chat.Type!="private"||message.Chat.Id!=message.From.Id||!IsPrimaryOwner(message.From.Id)||text.StartsWith('/'))
        {PurchaseCostDrafts.TryRemove(key,out _);return false;}
        if(draft.Until<DateTime.UtcNow){PurchaseCostDrafts.TryRemove(key,out _);await PurchaseCostPrompt(message.Chat.Id,"مهلت ثبت تمام شده؛ دوباره از منو شروع کنید.",ct);return true;}
        if(draft.Step=="search")
        {
            if(text.Length is <1 or >150){await PurchaseCostPrompt(message.Chat.Id,"نام یا کد معتبر با حداکثر ۱۵۰ نویسه وارد کنید.",ct);return true;}
            await SearchPurchaseCostAsync(message.Chat.Id,draft with{Query=text,Page=0},ct);return true;
        }
        if(draft.Step is not ("rate" or "amount"))
        {await PurchaseCostPrompt(message.Chat.Id,"مبلغ برای تأیید نمایش داده شده؛ از دکمهٔ تأیید استفاده کنید.",ct);return true;}
        if(!TryPurchaseAmount(text,out var amount))
        {await PurchaseCostPrompt(message.Chat.Id,"فقط مبلغ مثبت وارد کنید؛ ارقام فارسی پذیرفته می‌شوند. حداکثر چهار رقم اعشار.",ct);return true;}
        var token=Guid.NewGuid();var rate=draft.Step=="rate";
        PurchaseCostDrafts[key]=draft with{Step=rate?"rate-confirm":"price-confirm",Amount=amount,Token=token};
        var title=rate?$"هر ۱ درهم = {amount:0.####} تومان\nاز لحظهٔ تأیید تا ثبت نرخ بعدی معتبر خواهد بود.":
            $"قیمت خرید یک باتل کامل: {amount:0.####} درهم\nقیمت قبلی این لیست در سابقه نگه داشته می‌شود.\nقیمت فروش و فاکتور مشتری تغییر نمی‌کند.";
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),"👁 تأیید اطلاعات خرید\n"+title,
            [[new("✅ تأیید و ثبت",PurchaseCostPrefix+"save:"+token.ToString("N"))],[new("❌ انصراف",PurchaseCostPrefix+"cancel")]],ct);return true;
    }

    private async Task<decimal?> CurrentPurchasePriceAsync(SalesList list,CancellationToken ct)
    {
        var key=PerfumePurchaseCostService.VariantKey(list);
        return await _db.PerfumePurchasePrices.AsNoTracking().Where(p=>!p.IsDeleted&&p.VariantKey==key)
            .OrderByDescending(p=>p.SalesListId==list.Id).ThenByDescending(p=>p.CreatedAt).Select(p=>(decimal?)p.AmountAed).FirstOrDefaultAsync(ct);
    }

    private async Task SearchPurchaseCostAsync(long actor,PurchaseCostDraft draft,CancellationToken ct)
    {
        var query=draft.Query??"";int.TryParse(NormalizeNumber(query),out var code);query=query.Replace('ي','ی').Replace('ك','ک');
        var found=await _db.SalesLists.AsNoTracking().Include(l=>l.Perfume).Where(l=>!l.IsDeleted&&!l.IsInventoryOffer&&l.TotalVolume>0&&l.Status!=SalesListStatus.Cancelled&&
            (l.PublicCode==code||l.StablePublicCode==code||l.PersianName.Replace("ي","ی").Replace("ك","ک").Contains(query)||l.EnglishName.Contains(query)||l.Perfume.Name.Replace("ي","ی").Replace("ك","ک").Contains(query)))
            .OrderByDescending(l=>l.OpenDate).ThenByDescending(l=>l.CreatedAt).ThenBy(l=>l.Id).Skip(draft.Page*5).Take(6).ToArrayAsync(ct);
        var page=found.Take(5).ToArray();PurchaseCostDrafts[(actor,actor)]=draft with{Choices=page.Select(l=>l.Id).ToArray(),Until=DateTime.UtcNow.AddMinutes(20)};
        if(page.Length==0){await PurchaseCostPrompt(actor,"عطر پیدا نشد؛ نام یا کد دیگری بفرستید.",ct);return;}
        foreach(var list in page)
        {
            var price=await CurrentPurchasePriceAsync(list,ct);
            var caption=$"🧴 {list.PersianName}\n{list.EnglishName}\nکد لیست: {list.DisplayCode}\nحجم باتل: {list.TotalVolume:0.#} میل\n"+
                (price.HasValue?$"خرید ثبت‌شده: {price:0.####} درهم":"قیمت خرید ثبت نشده");
            IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons=[[new("انتخاب "+(string.IsNullOrWhiteSpace(list.PersianName)?list.EnglishName:list.PersianName)+" — "+list.DisplayCode,PurchaseCostPrefix+"pick:"+list.Id.ToString("N"))]];
            var photo=await SalesListPhotoResolver.ResolveSourcePostAsync(_db,list,ct)??await SalesListPhotoResolver.ResolveAsync(_db,list,ct);
            if(string.IsNullOrWhiteSpace(photo)||!(await _sender.SendPhotoWithKeyboardAsync(actor.ToString(),photo,caption,buttons,ct)).IsSuccessful)
                await _sender.SendInlineKeyboardAsync(actor.ToString(),caption+"\n📷 عکس ثبت‌شده در دسترس نیست.",buttons,ct);
        }
        var navigation=new List<IReadOnlyCollection<TelegramInlineButton>>();
        if(draft.Page>0)navigation.Add([new("⬅️ نتایج قبلی",PurchaseCostPrefix+"prev")]);
        if(found.Length>5)navigation.Add([new("➡️ نتایج بیشتر",PurchaseCostPrefix+"next")]);
        navigation.Add([new("❌ انصراف",PurchaseCostPrefix+"cancel")]);
        await _sender.SendInlineKeyboardAsync(actor.ToString(),"عطر را انتخاب کنید یا نام/کد دیگری بفرستید.",navigation,ct);
    }
}
