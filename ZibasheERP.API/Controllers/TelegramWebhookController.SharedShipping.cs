using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string SharedShippingPrefix="shipping:shared:";
    private sealed record SharedShippingDraft(string Step, DateTime Until, Guid? Primary=null, Guid? Reference=null,
        Guid[]? Choices=null, SharedShippingService.Preview? Preview=null, string? Token=null);
    private static readonly ConcurrentDictionary<(long Chat,long User),SharedShippingDraft> SharedShippingDrafts=new();
    private Task SharedPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new("❌ انصراف",SharedShippingPrefix+"cancel")]],ct);

    private async Task<bool> TryHandleSharedShippingCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Message is null||callback.Data?.StartsWith(SharedShippingPrefix,StringComparison.Ordinal)!=true)return false;
        var chat=callback.Message.Chat.Id;var key=(chat,callback.From.Id);var action=callback.Data[SharedShippingPrefix.Length..];
        if(action.StartsWith("items:")&&Guid.TryParseExact(action[6..],"N",out var request))
        {
            if(!await IsAuthorizedShippingAdminAsync(chat,callback.From.Id,ct))
            {await _sender.AnswerCallbackAsync(callback.Id,"دسترسی مسئول ارسال ندارید.",ct,true);return true;}
            try
            {
                var primary=await ShippingPackageService.ValidatePackageAsync(_db,request,null,ct);
                var items=await SharedShippingService.Items(_db,request).AsNoTracking().Where(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null).ToArrayAsync(ct);
                await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
                _shippingReferenceViewId=await OpenShippingReferenceViewAsync(new(primary.Id,request,items.Select(i=>i.Id).ToArray(),chat.ToString()),ct);
                try
                {
                    foreach(var part in SplitTelegramMessage(await SharedShippingItemsTextAsync(items,ct)))await ShippingReferenceTextAsync(chat,part,ct);
                    await FinishShippingReferenceViewAsync(chat,ct);
                }
                finally{_shippingReferenceViewId=null;}
            }
            catch(InvalidOperationException e){await _sender.AnswerCallbackAsync(callback.Id,e.Message,ct,true);}
            return true;
        }
        if(callback.Message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"ثبت پست مشترک فقط برای مدیر و حسابدار در خصوصی ربات است.",ct,true);return true;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        try
        {
            if(action=="start")
            {
                ClearAdminWorkflowDrafts(chat,callback.From.Id);SharedShippingDrafts[key]=new("primary",DateTime.UtcNow.AddMinutes(30));
                await SharedPrompt(chat,"📦 پست مشترک\nآیدی گیرندهٔ اصلی را وارد کنید؛ آدرس او باید قبلاً در گروه آماده‌سازی بسته ثبت شده باشد.",ct);return true;
            }
            if(action=="cancel")
            {SharedShippingDrafts.TryRemove(key,out _);await _sender.SendInlineKeyboardAsync(chat.ToString(),"فرایند بسته شد.",[[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);return true;}
            if(!SharedShippingDrafts.TryGetValue(key,out var draft)||draft.Until<DateTime.UtcNow)throw new InvalidOperationException("فرایند منقضی شده؛ دوباره شروع کنید.");
            if(action.StartsWith("address:")&&Guid.TryParseExact(action[8..],"N",out var selected)&&draft.Step=="address"&&draft.Choices?.Contains(selected)==true)
            {await SelectSharedAddressAsync(chat,callback.From.Id,draft,selected,ct);return true;}
            if(draft.Step!="confirm"||draft.Preview is null||draft.Reference is null||action!="confirm:"+draft.Token)
                throw new InvalidOperationException("دکمهٔ پیش‌نمایش معتبر نیست؛ دوباره شروع کنید.");
            var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==draft.Reference&&n.Channel==ShippingReferenceChannel&&!n.IsDeleted,ct);
            var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
            await new SharedShippingService(_db).ConfirmAsync(draft.Preview,callback.From.Id,context.MessageId??0,ct,row.Id);
            SharedShippingDrafts.TryRemove(key,out _);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"✅ پست مشترک ثبت شد.\nاقلام هر مشتری با حفظ مالکیت خودش به بستهٔ گیرندهٔ اصلی متصل شدند. همان پیام آدرس از صف به‌روز می‌شود؛ با ثبت رهگیری برای نفر اصلی، فقط اقلام همین بسته ارسال‌شده می‌شوند.",
                [[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);
        }
        catch(InvalidOperationException e){await SharedPrompt(chat,e.Message,ct);}
        return true;
    }

    private async Task SelectSharedAddressAsync(long chat,long user,SharedShippingDraft draft,Guid reference,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==reference&&n.Channel==ShippingReferenceChannel&&!n.IsDeleted,ct);
        var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
        if(context.CustomerId!=draft.Primary||context.Kind!="root"||context.AddressId is null||context.MessageId is null||!context.Installed||row.Recipient!=_options.ShippingChatId.Trim())
            throw new InvalidOperationException("مرجع آدرس معتبر نیست؛ آدرس را دوباره در گروه آماده‌سازی ثبت کنید.");
        SharedShippingDrafts[(chat,user)]=draft with{Step="members",Reference=reference};
        await SharedPrompt(chat,"آیدی نفرات همراه را بفرستید؛ هر آیدی در یک خط یا با فاصله.\nفقط اقلام آمادهٔ ارسال آن‌ها اضافه می‌شوند. آیدی نفر اصلی را دوباره ننویسید.",ct);
    }

    private async Task<bool> TryHandleSharedShippingMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!SharedShippingDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var draft))return false;
        var chat=message.Chat.Id;var key=(chat,message.From.Id);var text=(message.Text??"").Trim();
        if(message.Chat.Type!="private"||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct)||text.StartsWith('/'))
        {SharedShippingDrafts.TryRemove(key,out _);return false;}
        try
        {
            if(draft.Until<DateTime.UtcNow)throw new InvalidOperationException("فرایند منقضی شده؛ دوباره شروع کنید.");
            var service=new SharedShippingService(_db);
            if(draft.Step=="primary")
            {
                var customer=await service.ResolveCustomerAsync(text,ct);
                var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.CustomerId==customer.Id&&n.Recipient==_options.ShippingChatId.Trim())
                    .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
                var options=new List<(Guid Id,ShippingReferenceContext Context)>();var seen=new HashSet<Guid>();
                foreach(var row in rows)
                {
                    var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
                    if(context.Kind!="root"||context.AddressId is null||context.MessageId is null||!context.Installed||context.CustomerId!=customer.Id)continue;
                    var identity=context.RequestId??context.AddressId.Value;if(!seen.Add(identity))continue;
                    if(context.RequestId.HasValue)
                    {
                        var assigned=await SharedShippingService.Items(_db,context.RequestId.Value).AsNoTracking().ToArrayAsync(ct);
                        if(assigned.Length==0||assigned.Any(i=>i.ShippedAt!=null||i.FulfillmentStatus==OrderItemFulfillmentStatus.Shipped))continue;
                        if((await ShippingPackageService.ValidatePackageAsync(_db,context.RequestId.Value,null,ct)).Id!=customer.Id)continue;
                    }
                    options.Add((row.Id,context));
                }
                if(options.Count==0)throw new InvalidOperationException("آدرس بازِ ثبت‌شده برای این مشتری پیدا نشد؛ ابتدا آدرسش را در گروه آماده‌سازی بسته ثبت کنید.");
                draft=draft with{Primary=customer.Id,Step="address",Choices=options.Select(o=>o.Id).ToArray()};SharedShippingDrafts[key]=draft;
                if(options.Count==1)await SelectSharedAddressAsync(chat,message.From.Id,draft,options[0].Id,ct);
                else await _sender.SendInlineKeyboardAsync(chat.ToString(),"این مشتری چند آدرس/بستهٔ باز دارد؛ مقصد پست مشترک را انتخاب کنید:",options.Take(20).Select((o,index)=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton($"📦 بستهٔ {index+1}",SharedShippingPrefix+"address:"+o.Id.ToString("N"))}).Append(new[]{new TelegramInlineButton("❌ انصراف",SharedShippingPrefix+"cancel")}).ToArray(),ct);
                return true;
            }
            if(draft.Step!="members"||draft.Reference is null)throw new InvalidOperationException("از دکمه‌های مرحلهٔ فعلی استفاده کنید.");
            var identities=Regex.Split(text,@"[\s,،;؛]+").Where(s=>s.Length>0).ToArray();
            if(identities.Length is <1 or >19)throw new InvalidOperationException("آیدی ۱ تا ۱۹ نفر همراه را وارد کنید.");
            var memberIds=new List<Guid>();foreach(var identity in identities)memberIds.Add((await service.ResolveCustomerAsync(identity,ct)).Id);
            var reference=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==draft.Reference&&!n.IsDeleted,ct);
            var selectedContext=JsonSerializer.Deserialize<ShippingReferenceContext>(reference.Payload)!;
            var request=await service.ResolvePrimaryRequestAsync(draft.Primary!.Value,selectedContext.RequestId,ct);
            var preview=await service.PreviewAsync(request,draft.Primary.Value,selectedContext.AddressId!.Value,memberIds.ToArray(),message.From.Id,ct);
            var items=await _db.OrderItems.AsNoTracking().Where(i=>preview.ItemIds.Contains(i.Id)).Include(i=>i.Order).ThenInclude(o=>o!.Customer)
                .Include(i=>i.Perfume).Include(i=>i.SalesList).Include(i=>i.Bottle).Include(i=>i.SourceSalesListRequest).ToArrayAsync(ct);
            var primary=await _db.Customers.AsNoTracking().SingleAsync(c=>c.Id==draft.Primary,ct);
            var token=Guid.NewGuid().ToString("N")[..12];SharedShippingDrafts[key]=draft with{Step="confirm",Preview=preview,Token=token};
            var caption=$"📦 پیش‌نمایش پست مشترک\nگیرندهٔ اصلی: {OrderCustomerLabel(primary)}\nحساب‌ها و فاکتورها جدا می‌مانند.\n\n"+await SharedShippingItemsTextAsync(items,ct);
            foreach(var part in SplitTelegramMessage(caption))await _sender.SendAsync(chat.ToString(),part,ct);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"عضویت اقلام بالا در بستهٔ گیرندهٔ اصلی تأیید شود؟",[[new("✅ ثبت پست مشترک",SharedShippingPrefix+"confirm:"+token)],[new("❌ انصراف",SharedShippingPrefix+"cancel")]],ct);
        }
        catch(InvalidOperationException e){await SharedPrompt(chat,e.Message,ct);}
        return true;
    }

    private async Task<string> SharedShippingItemsTextAsync(OrderItem[] items,CancellationToken ct,bool compact=false)
    {
        var labels=await ShippingRecipientLabelsAsync(items,ct);
        return "📦 اقلام آمادهٔ پست مشترک\n\n"+string.Join("\n\n",items.GroupBy(i=>labels[i.Id]).Select(g=>
            $"👤 {g.Key} — {g.Count()} آیتم"+(compact?"":"\n"+string.Join("\n",g.Select((i,index)=>FormatShippingItemLine(i,index))))))+
            $"\n\n🔴 تعداد آیتم‌ها: {items.Length}";
    }

    internal async Task RefreshSharedShippingAddressAsync(NotificationOutbox job,CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(job.Payload);var id=payload.RootElement.GetProperty("ReferenceId").GetGuid();
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==ShippingReferenceChannel,ct);
        var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
        if(row.CustomerId!=job.CustomerId||row.Recipient!=job.Recipient||job.Recipient!=_options.ShippingChatId.Trim()||context.MessageId is null||context.RequestId is null)
            throw new InvalidOperationException("Shared parcel address binding changed");
        await using var guard=await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"shared-shipping:"+context.RequestId.Value},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Shared parcel busy',1;",ct);
        var customer=await ShippingPackageService.ValidatePackageAsync(_db,context.RequestId.Value,context.CustomerId,ct);
        var parcel=await SharedShippingService.ReadAsync(_db,context.RequestId.Value,ct)??throw new InvalidOperationException("Shared parcel missing");
        if(parcel.ClosedAt!=null)return;
        var address=await _db.Addresses.AsNoTracking().SingleAsync(a=>a.Id==parcel.AddressId&&a.CustomerId==customer.Id&&!a.IsDeleted,ct);
        var items=await SharedShippingService.Items(_db,context.RequestId.Value).AsNoTracking().Where(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null).ToArrayAsync(ct);
        var header=$"📦 درخواست ارسال مشترک\nگیرندهٔ اصلی: {OrderCustomerLabel(customer)}\n\n{FormatAddressForDisplay(address)}\n\n";
        var text=header+await SharedShippingItemsTextAsync(items,ct);
        if(text.Length>3900)text=header+await SharedShippingItemsTextAsync(items,ct,true)+"\nبرای مشاهدهٔ تمام اقلام، دکمهٔ زیر را بزنید.";
        var buttons=new List<IReadOnlyCollection<TelegramInlineButton>> {
            new[]{new TelegramInlineButton("📦 اقلام پست مشترک",SharedShippingPrefix+"items:"+context.RequestId.Value.ToString("N"))},
            new[]{new TelegramInlineButton("☑️ انتخاب برای گزارش کلی","shipping:select:"+context.RequestId.Value.ToString("N"))},
            new[]{new TelegramInlineButton("📊 گزارش کلی انتخاب‌ها","shipping:batchreport")},
            new[]{new TelegramInlineButton("✅ ارسال شد","shipping:sent:"+context.RequestId.Value.ToString("N")),new TelegramInlineButton("📸 ارسال کد رهگیری","shipping:trackingbatch:"+context.RequestId.Value.ToString("N"))} };
        var previous=await FindShippingPackAppendBaseAsync(context,items,ct);
        buttons.AddRange(previous is null?ShippingReferenceButtons(row.Id):ShippingReferenceButtonsWithPhotoCount(row.Id,previous.Data.PhotoFileIds.Length));
        var result=await _sender.EditTextWithKeyboardAsync(row.Recipient,context.MessageId.Value,text,buttons,ct);
        if(!result.IsSuccessful&&result.Error?.Contains("message is not modified",StringComparison.OrdinalIgnoreCase)!=true)throw new InvalidOperationException("Shared parcel address refresh failed");
        await guard.CommitAsync(ct);
    }
}
