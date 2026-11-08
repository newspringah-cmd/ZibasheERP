using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string TrackingSelectionChannel="TrackingRecipientSelection";
    private const string ManualTrackingMatch="تطبیق دستی تأییدشده با آدرس آماده‌سازی";
    private sealed record TrackingRecipientSelection(Guid DispatchId,long Actor,long Chat,DateTime Until,string Fingerprint,Guid[] Roots,
        int? Selected=null,string? SelectedScope=null,bool Applied=false,string? Token=null);
    private static string TrackingSelectionFingerprint(TrackingDispatch d)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{d.Id}:{d.ImportBatchId}:{d.TrackingCode}:{d.RecipientName}:{d.CustomerId}:{d.ShippingRequestId}")));
    private static string TrackingSelectionScope(PreparedTrackingAddress p)=>$"{p.ReferenceId}:{p.CustomerId}:{p.AddressId}:{p.RequestId}:{p.MessageId}:"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{p.FullAddress}:{p.ReceiverName}:{p.Username}")));
    private static string PreparedPostedAt(PreparedTrackingAddress p)=>FormatShippingPersianDate(p.PostedAt)+" — "+
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(p.PostedAt,DateTimeKind.Utc),"Asia/Tehran").ToString("HH:mm");
    private string PreparedAddressLink(PreparedTrackingAddress p)=>InventoryOffersController.PostUrl(_options.ShippingChatId,p.MessageId);
    private static string PreparedName(PreparedTrackingAddress p)=>TrackingImportService.PreparedRecipientNames(p).FirstOrDefault()??p.ReceiverName;
    private string TrackingSimilarityHints(TrackingDispatch dispatch,IEnumerable<PreparedTrackingAddress> prepared)
    {
        var choices=prepared.Select(p=>new{Address=p,Names=TrackingImportService.PreparedSimilarNames(dispatch.RecipientName,p),Score=TrackingImportService.PreparedSuggestionScore(dispatch.RecipientName,p)})
            .Where(p=>p.Names.Length>0||p.Score>=4.5).OrderByDescending(p=>p.Score).ThenByDescending(p=>p.Address.PostedAt)
            .DistinctBy(p=>p.Address.CustomerId).Take(2).ToArray();
        string Short(string text,int length)=>text.Length>length?text[..length]+"…":text;
        return string.Join("\n",choices.Select(p=>
        {
            var identity=string.IsNullOrWhiteSpace(p.Address.Username)?"گروه مشتری":("@"+Short(p.Address.Username.TrimStart('@'),40));
            var named=p.Names.Length>0&&TrackingImportService.PreparedRecipientNames(p.Address).Contains(p.Names[0]);
            var description=named?$"شباهت نام با {identity} — نام ثبت‌شده: {Short(p.Names[0],60)}":
                $"عبارت مشابه در آدرس {identity}: {Short(dispatch.RecipientName,60)}";
            return "🔎 "+description+"\n"+PreparedAddressLink(p.Address);
        }));
    }
    private string TrackingCaptionWithSuggestions(TrackingDispatch dispatch,string caption,PreparedTrackingAddress[] prepared)
    {
        if(dispatch.Status!=TrackingDispatchStatus.NeedsReview||dispatch.SendingStartedAt.HasValue)return caption;
        var hints=TrackingSimilarityHints(dispatch,prepared);if(hints.Length==0)return caption;
        if(caption.Length+hints.Length+2>1024)
            caption=$"⚠️ نام خوانده‌شده: {dispatch.RecipientName[..Math.Min(130,dispatch.RecipientName.Length)]}\nکد: {dispatch.TrackingCode}\n"+
                (dispatch.MatchNotes??"")[..Math.Min(180,(dispatch.MatchNotes??"").Length)];
        return caption+"\n\n"+hints;
    }

    private async Task AssertTrackingSelectableAsync(TrackingDispatch dispatch,CancellationToken ct)
    {
        if(!await _db.TrackingImportBatches.AnyAsync(b=>b.Id==dispatch.ImportBatchId&&!b.IsDeleted,ct)||dispatch.IsDeleted||dispatch.Status is not (TrackingDispatchStatus.NeedsReview or TrackingDispatchStatus.Ready)||dispatch.SentAt.HasValue||dispatch.SendingStartedAt.HasValue||
            await _db.TrackingDispatchDeliveries.AnyAsync(d=>d.TrackingDispatchId==dispatch.Id&&(d.AttemptedAt!=null||d.SentAt!=null),ct)||
            await _db.NotificationOutbox.AnyAsync(n=>n.Id==dispatch.Id&&n.Channel=="TrackingDispatch",ct))
            throw new InvalidOperationException("گیرندهٔ مورد ارسال‌شده یا دارای سابقهٔ تلاش ارسال، قابل تغییر نیست.");
    }

    private async Task<Guid?> ResolvePreparedTrackingRequestAsync(PreparedTrackingAddress choice,CancellationToken ct,DateTime? sourceAsOf=null)
    {
        var request=choice.RequestId;
        if(request is null)
        {
            var ids=await _db.OrderItems.AsNoTracking().Where(i=>!i.IsDeleted&&i.ShippingRequestId!=null&&i.ShippedAt==null&&
                i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&(i.ShippingRequestedAt??i.CreatedAt)<=(sourceAsOf??DateTime.UtcNow)&&i.Order!=null&&!i.Order.IsDeleted&&
                (i.Order.CustomerId==choice.CustomerId||i.Order.DeliveryAddress!=null&&i.Order.DeliveryAddress.CustomerId==choice.CustomerId))
                .Select(i=>i.ShippingRequestId!.Value).Distinct().ToArrayAsync(ct);
            if(ids.Length>1)throw new InvalidOperationException("این مشتری چند بستهٔ باز دارد؛ آدرسی را انتخاب کنید که به درخواست ارسال همان بسته متصل است.");
            request=ids.Length==1?ids[0]:null;
        }
        if(request is Guid id)await ShippingPackageService.ValidatePackageAsync(_db,id,choice.CustomerId,ct);
        return request;
    }

    private async Task<bool> TryHandleTrackingRecipientSelectionAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Data?.StartsWith("trackingimport:select-",StringComparison.Ordinal)!=true)return false;
        var actor=callback.From.Id;var chat=callback.Message!.Chat.Id;var parts=callback.Data.Split(':');
        try
        {
            if(parts.Length<3||!Guid.TryParseExact(parts[2],"N",out var id))throw new InvalidOperationException("دکمهٔ انتخاب گیرنده معتبر نیست.");
            await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
            if(parts[1]=="select-list"&&parts.Length==4&&int.TryParse(parts[3],out var listPage)&&listPage>=0)
            {
                var reviews=await _db.TrackingDispatches.AsNoTracking().Where(d=>d.ImportBatchId==id&&!d.IsDeleted&&d.Status==TrackingDispatchStatus.NeedsReview&&
                    d.SentAt==null&&d.SendingStartedAt==null&&!d.Deliveries.Any(p=>p.AttemptedAt!=null||p.SentAt!=null))
                    .OrderBy(d=>d.RecipientName).ThenBy(d=>d.Id).ToArrayAsync(ct);
                if(reviews.Length==0){await ReplyAsync(chat,"مورد بررسیِ بدون سابقهٔ ارسال برای این سری باقی نمانده است.",ct);return true;}
                if(listPage>(reviews.Length-1)/6)throw new InvalidOperationException("صفحهٔ انتخاب معتبر نیست.");
                var visible=reviews.Skip(listPage*6).Take(6).ToArray();
                var prepared=await TrackingPreparedAddresses.LoadAsync(_db,_options.ShippingChatId.Trim(),ct);
                var details=string.Join("\n\n",visible.Select(d=>
                {
                    var hints=TrackingSimilarityHints(d,prepared);
                    return $"• {d.RecipientName[..Math.Min(150,d.RecipientName.Length)]}\nکد: {d.TrackingCode}\n"+
                        (hints.Length>0?hints:"آیدی مشابهِ قابل پیشنهاد پیدا نشد؛ از فهرست آدرس‌ها دستی بررسی کنید.");
                }));
                var buttons=visible.Select(d=>(IReadOnlyCollection<TelegramInlineButton>)new[]{new TelegramInlineButton(
                    d.RecipientName+" — "+(d.TrackingCode.Length>6?d.TrackingCode[^6..]:d.TrackingCode),"trackingimport:select-open:"+d.Id.ToString("N"))}).ToList();
                if(listPage>0)buttons.Add([new("⬅️ قبلی",$"trackingimport:select-list:{id:N}:{listPage-1}")]);
                if((listPage+1)*6<reviews.Length)buttons.Add([new("➡️ موارد بیشتر",$"trackingimport:select-list:{id:N}:{listPage+1}")]);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),"🔎 نام‌ها و آیدی‌های مشابه\nپیشنهادها را با آیدی و آدرس اصلی چک کنید؛ اگر همان شخص بود، ردیف را انتخاب و گیرنده را تأیید کنید. ارسال کد با دکمهٔ جداگانه انجام می‌شود.\n\n"+details,buttons,ct);return true;
            }
            if(parts[1]=="select-open")
            {
                var dispatch=await _db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==id&&!d.IsDeleted,ct);await AssertTrackingSelectableAsync(dispatch,ct);
                var choices=await TrackingPreparedAddresses.LoadAsync(_db,_options.ShippingChatId.Trim(),ct);
                choices=choices.OrderByDescending(p=>TrackingImportService.PreparedSuggestionScore(dispatch.RecipientName,p)).ThenByDescending(p=>p.PostedAt).ToArray();
                var data=new TrackingRecipientSelection(id,actor,chat,DateTime.UtcNow.AddMinutes(20),TrackingSelectionFingerprint(dispatch),choices.Select(p=>p.ReferenceId).ToArray());
                var key=Guid.NewGuid();_db.NotificationOutbox.Add(new(){Id=key,CustomerId=Guid.Empty,Channel=TrackingSelectionChannel,EventType="ChoosePreparedTrackingRecipient",Recipient=chat.ToString(),
                    Payload=JsonSerializer.Serialize(data),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});await _db.SaveChangesAsync(ct);
                await ShowPreparedTrackingChoicesAsync(key,data,0,choices,ct);return true;
            }
            var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Channel==TrackingSelectionChannel&&!n.IsDeleted,ct)
                ??throw new InvalidOperationException("فرم انتخاب پیدا نشد.");
            var draft=JsonSerializer.Deserialize<TrackingRecipientSelection>(row.Payload)!;
            if(draft.Actor!=actor||draft.Chat!=chat||row.Recipient!=chat.ToString())throw new InvalidOperationException("این فرم متعلق به همین مدیر و همین گفتگو نیست.");
            if(draft.Applied){await ReplyAsync(chat,"این انتخاب قبلاً ثبت شده؛ کد دوباره ارسال نشده است.",ct);return true;}
            if(draft.Until<DateTime.UtcNow)throw new InvalidOperationException("فرم منقضی شده؛ از انتخاب گیرنده دوباره شروع کنید.");
            var current=await _db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==draft.DispatchId,ct);await AssertTrackingSelectableAsync(current,ct);
            if(TrackingSelectionFingerprint(current)!=draft.Fingerprint)throw new InvalidOperationException("این ردیف تغییر کرده؛ از انتخاب گیرنده دوباره شروع کنید.");
            var sourceAsOf=await _db.TrackingImportBatches.AsNoTracking().Where(b=>b.Id==current.ImportBatchId).Select(b=>b.CreatedAt).SingleAsync(ct);
            var available=await TrackingPreparedAddresses.LoadAsync(_db,_options.ShippingChatId.Trim(),ct);
            if(parts[1]=="select-page"&&parts.Length==4&&int.TryParse(parts[3],out var page)&&page>=0&&page<Math.Max(1,(draft.Roots.Length+5)/6))
            {await ShowPreparedTrackingChoicesAsync(id,draft,page,available,ct);return true;}
            if(parts[1]=="select-pick"&&parts.Length==4&&int.TryParse(parts[3],out var index)&&index>=0&&index<draft.Roots.Length)
            {
                var choice=available.SingleOrDefault(p=>p.ReferenceId==draft.Roots[index])??throw new InvalidOperationException("این آدرس دیگر آماده یا متصل نیست.");
                var request=await ResolvePreparedTrackingRequestAsync(choice,ct,sourceAsOf);var token=Guid.NewGuid().ToString("N")[..6];
                await MutateShippingPayloadAsync<TrackingRecipientSelection>(id,TrackingSelectionChannel,d=>d with{Selected=index,SelectedScope=TrackingSelectionScope(choice)+":"+request,Token=token},ct);
                await _sender.SendInlineKeyboardAsync(chat.ToString(),$"👁 تأیید گیرندهٔ کد رهگیری\nکد: {current.TrackingCode}\nنام داخل فایل: {current.RecipientName}\n\nگیرندهٔ انتخابی: {PreparedName(choice)}\nآیدی: @{choice.Username.TrimStart('@')}\n{choice.City}\nثبت آدرس: {PreparedPostedAt(choice)}\n{PreparedAddressLink(choice)}\n\nنام و آدرس را با فایل پست بررسی کنید. این تأیید فقط گیرنده را ثبت می‌کند و کدی ارسال نمی‌شود.",
                    [[new("✅ تأیید گیرنده و آماده‌سازی","trackingimport:select-ok:"+id.ToString("N")+":"+token)],[new("↩️ بازگشت به پیشنهادها","trackingimport:select-page:"+id.ToString("N")+":0")]],ct);return true;
            }
            if(parts[1]!="select-ok"||parts.Length!=4||parts[3]!=draft.Token||draft.Selected is not int selected)throw new InvalidOperationException("ابتدا آدرس را انتخاب و آخرین پیش‌نمایش را بررسی کنید.");
            var target=available.SingleOrDefault(p=>p.ReferenceId==draft.Roots[selected])??throw new InvalidOperationException("آدرس مقصد دیگر آماده نیست.");
            var selectedRequest=await ResolvePreparedTrackingRequestAsync(target,ct,sourceAsOf);
            if(TrackingSelectionScope(target)+":"+selectedRequest!=draft.SelectedScope)throw new InvalidOperationException("درخواست یا آدرس تغییر کرده؛ انتخاب گیرنده را دوباره شروع کنید.");
            await using var tx=await _db.Database.BeginTransactionAsync(ct);
            var selectionRow=await _db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
            var lockedSelection=JsonSerializer.Deserialize<TrackingRecipientSelection>(selectionRow.Payload)!;
            if(lockedSelection.Applied){await tx.CommitAsync(ct);return true;}
            if(lockedSelection.Token!=draft.Token||lockedSelection.SelectedScope!=draft.SelectedScope)throw new InvalidOperationException("پیش‌نمایش با انتخاب جدید جایگزین شده است.");
            var locked=await _db.TrackingDispatches.FromSqlInterpolated($"SELECT * FROM TrackingDispatches WITH(UPDLOCK,HOLDLOCK) WHERE Id={draft.DispatchId}").SingleAsync(ct);
            await AssertTrackingSelectableAsync(locked,ct);
            if(TrackingSelectionFingerprint(locked)!=draft.Fingerprint)throw new InvalidOperationException("ردیف پس از پیش‌نمایش تغییر کرده؛ انتخاب را دوباره شروع کنید.");
            var package=await ResolvePreparedTrackingRequestAsync(target,ct,sourceAsOf);
            if(package!=selectedRequest)throw new InvalidOperationException("بسته پس از پیش‌نمایش تغییر کرده است.");
            if(package.HasValue&&await _db.TrackingDispatches.AnyAsync(d=>d.Id!=locked.Id&&!d.IsDeleted&&d.ShippingRequestId==package&&
                (d.Status==TrackingDispatchStatus.Ready||d.Status==TrackingDispatchStatus.Sending||d.Status==TrackingDispatchStatus.Sent||d.Deliveries.Any(p=>p.AttemptedAt!=null||p.SentAt!=null)),ct))
                throw new InvalidOperationException("برای این بسته کد دیگری آماده یا ارسال شده است؛ ثبت تکراری متوقف شد.");
            locked.CustomerId=target.CustomerId;locked.ShippingRequestId=package;locked.Status=TrackingDispatchStatus.Ready;locked.MatchNotes=$"{ManualTrackingMatch}: {PreparedName(target)} — @{target.Username.TrimStart('@')}";locked.LastError=null;locked.UpdatedAt=DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await MutateShippingPayloadAsync<TrackingRecipientSelection>(id,TrackingSelectionChannel,d=>d with{Applied=true},ct);
            _db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=target.CustomerId,Channel="TrackingRecipientChoiceAudit",EventType="TrackingRecipientSelectedByAdmin",Recipient=actor.ToString(),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow,
                Payload=JsonSerializer.Serialize(new{SelectionId=id,DispatchId=locked.Id,OriginalRecipientName=locked.RecipientName,locked.TrackingCode,target.ReferenceId,target.AddressId,CustomerId=target.CustomerId,ShippingRequestId=package,Actor=actor})});
            await _db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            await _sender.SendInlineKeyboardAsync(chat.ToString(),$"✅ گیرنده ثبت شد: {PreparedName(target)} — @{target.Username.TrimStart('@')}\nکد {locked.TrackingCode} آماده است؛ هنوز برای مشتری ارسال نشده است.",BuildTrackingDispatchButtons(locked),ct);
        }
        catch(InvalidOperationException e){await ReplyAsync(chat,"⚠️ "+e.Message,ct);}
        return true;
    }

    private async Task ShowPreparedTrackingChoicesAsync(Guid id,TrackingRecipientSelection draft,int page,PreparedTrackingAddress[] available,CancellationToken ct)
    {
        var rows=new List<IReadOnlyCollection<TelegramInlineButton>>();var dispatch=await _db.TrackingDispatches.AsNoTracking().SingleAsync(d=>d.Id==draft.DispatchId,ct);
        var hints=TrackingSimilarityHints(dispatch,available);
        var text="🔎 انتخاب گیرنده از آدرس‌های آماده‌سازی\n"+
            (hints.Length>0?"این نام با آیدی‌های زیر شباهت دارد؛ آیدی و آدرس را چک کنید و اگر همان شخص است انتخاب و تأیید کنید.\n"+hints:
                "نام کافی برای پیشنهاد آیدی نداریم؛ آدرس صحیح را با فایل پست بررسی و انتخاب کنید.")+"\n\nآدرس‌های موجود در این صفحه:\n";
        for(var i=page*6;i<Math.Min(draft.Roots.Length,page*6+6);i++)
        {
            var choice=available.SingleOrDefault(p=>p.ReferenceId==draft.Roots[i]);if(choice is null)continue;
            var label=$"{PreparedName(choice)} — @{choice.Username.TrimStart('@')}";if(label.Length>110)label=label[..110];
            rows.Add([new(label,$"trackingimport:select-pick:{id:N}:{i}")]);text+=$"\n{i+1}. {label}\nثبت آدرس: {PreparedPostedAt(choice)}\n{PreparedAddressLink(choice)}\n";
        }
        if(page>0)rows.Add([new("⬅️ قبلی",$"trackingimport:select-page:{id:N}:{page-1}")]);
        if((page+1)*6<draft.Roots.Length)rows.Add([new("➡️ آدرس‌های بیشتر",$"trackingimport:select-page:{id:N}:{page+1}")]);
        if(rows.Count==0)text+="\nآدرس آماده با گروه مشتریِ متصل پیدا نشد؛ اتصال آیدی یا ثبت آدرس مشتری را بررسی کنید.";
        await _sender.SendInlineKeyboardAsync(draft.Chat.ToString(),text,rows,ct);
    }
}
