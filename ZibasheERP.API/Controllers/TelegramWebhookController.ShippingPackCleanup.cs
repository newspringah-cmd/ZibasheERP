using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private async Task<IReadOnlyCollection<TelegramInlineButton>[]> ShippingPackAddressNavigationAsync(ShippingReferenceContext context,CancellationToken ct)
    {
        string? Link(long? messageId)=>context.ChatId.StartsWith("-100",StringComparison.Ordinal)&&
            context.ChatId.Length>4&&context.ChatId[4..].All(char.IsAsciiDigit)&&messageId is >0
            ?$"https://t.me/c/{context.ChatId[4..]}/{messageId.Value}":null;
        var buttons=new List<IReadOnlyCollection<TelegramInlineButton>>();
        var current=Link(context.MessageId);if(current is null)return [];
        buttons.Add([new("📍 بازگشت به آدرس مشتری",Url:current)]);
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&
            n.EventType=="AddressReferences"&&n.Recipient==context.ChatId&&n.CustomerId!=context.CustomerId)
            .OrderByDescending(n=>n.CreatedAt).Take(1000).ToArrayAsync(ct);
        var candidates=rows.Select(n=>(Row:n,Data:JsonSerializer.Deserialize<ShippingReferenceContext>(n.Payload)!))
            .Where(x=>x.Data.Kind=="root"&&x.Data.Installed&&x.Data.CustomerId==x.Row.CustomerId&&x.Data.ChatId==context.ChatId&&
                x.Data.MessageId>context.MessageId&&x.Data.RequestId.HasValue).OrderBy(x=>x.Data.MessageId).Take(50);
        foreach(var candidate in candidates)
        {
            try
            {
                if((await LoadShippingReferenceItemsAsync(candidate.Data,ct)).Length==0)continue;
                buttons.Add([new("➡️ آدرس بعدی آماده‌سازی",Url:Link(candidate.Data.MessageId))]);break;
            }
            catch(InvalidOperationException){/* An expired or invalid parcel is not a navigation target. */}
        }
        return buttons.ToArray();
    }
    private async Task RecordShippingPackUiAsync(Guid id,TelegramSendResult sent,CancellationToken ct,bool question=false,bool control=false,int questionCount=0)
    {
        if(!sent.IsSuccessful||sent.MessageId is not >0)return;
        var message=sent.MessageId.Value;
        var draft=await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with
        {
            UiMessageIds=(d.UiMessageIds??[]).Append(message).Distinct().ToArray(),
            QuestionIds=question?(d.QuestionIds??[]).Append(message).Distinct().ToArray():d.QuestionIds,
            QuestionId=question&&questionCount>=d.QuestionPhotoCount?message:d.QuestionId,
            QuestionPhotoCount=question?Math.Max(questionCount,d.QuestionPhotoCount):d.QuestionPhotoCount,
            CleanupControlIds=control?(d.CleanupControlIds??[]).Append(message).Distinct().ToArray():d.CleanupControlIds,
            CleanupCompletedAt=d.CleanupRequested?null:d.CleanupCompletedAt
        },ct);
        // A slow acknowledgement can finish after the operator pressed Cleanup.
        // It belongs to the same exact recorded UI bundle and is cleaned too.
        if(question&&draft.QuestionId!=message)await _sender.EditReplyMarkupAsync(draft.Reference.ChatId,message,[],ct);
        if(draft.CleanupRequested)await DeleteShippingPackExtrasAsync(id,draft,ct);
    }
    private async Task SendShippingPackMoreQuestionAsync(Guid id,ShippingPackPhotoDraft draft,CancellationToken ct)
    {
        var count=draft.Files?.Length??0;
        var buttons=new List<IReadOnlyCollection<TelegramInlineButton>>();
        if(count<3)buttons.Add([new("بله",ShippingReferencePrefix+"add:"+id.ToString("N")),new("خیر",ShippingReferencePrefix+"send:"+id.ToString("N"))]);
        else buttons.Add([new("خیر",ShippingReferencePrefix+"send:"+id.ToString("N"))]);
        buttons.Add([new("❌ انصراف",ShippingReferencePrefix+"cancel:"+id.ToString("N"))]);
        var text=$"✅ {count} عکس جدید برای این بسته ثبت شد.\nآیا عکس دیگری دارید؟"+
            (draft.ExistingPhotoCount>0?$"\nتعداد کل همراه عکس‌های قبلی: {draft.ExistingPhotoCount+count}":"")+
            (count<3?"\nبا «بله» عکس بعدی را اضافه کنید؛ با «خیر» عکس‌ها برای مشتری ارسال می‌شوند.":"\nسقف ۳ عکس تکمیل شده؛ برای ارسال عکس‌ها «خیر» را بزنید.");
        var sent=await _sender.SendInlineKeyboardAsync(draft.Reference.ChatId,text,buttons,ct);
        await RecordShippingPackUiAsync(id,sent,ct,question:true,questionCount:count);
    }
    private static string ShippingPackDeliveryNotice(ShippingPackApprovalData data)=>
        $"✅ {data.PhotoFileIds.Length} عکس بسته برای مشتری ارسال شد."+
        (data.ConfirmedAt.HasValue?" تأیید مشتری ثبت شده است.":" منتظر تأیید مشتری هستیم.");
    private static bool ShippingPackPhotosDelivered(ShippingPackApprovalData data)
    {
        if(!data.SentAt.HasValue||data.PhotoFileIds.Length<1||data.Deliveries.Length==0)return false;
        return data.Deliveries.GroupBy(d=>d.ChatId).All(g=>Enumerable.Range(0,data.PhotoFileIds.Length)
            .All(index=>g.Any(d=>d.Index==index&&d.SentAt.HasValue)));
    }
    private async Task SendShippingPackDeliveryNoticeAsync(Guid approvalId,ShippingPackApprovalData data,CancellationToken ct)
    {
        if(!data.PhotoDraftId.HasValue)
        {await _sender.SendAsync(data.ShippingChatId,ShippingPackDeliveryNotice(data),ct);return;}
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==data.PhotoDraftId&&n.Channel==ShippingPackDraftChannel&&!n.IsDeleted,ct);
        var draft=row is null?null:JsonSerializer.Deserialize<ShippingPackPhotoDraft>(row.Payload);
        if(draft is null||draft.ApprovalId!=approvalId||draft.Reference.CustomerId!=data.CustomerId||draft.Reference.ChatId!=data.ShippingChatId||!ShippingPackPhotosDelivered(data))return;
        var buttons=new List<IReadOnlyCollection<TelegramInlineButton>>();
        buttons.Add([new("🧹 حذف پیام‌های اضافی",ShippingReferencePrefix+"packclear:"+row!.Id.ToString("N"))]);
        buttons.AddRange(await ShippingPackAddressNavigationAsync(draft.Reference,ct));
        var sent=await _sender.SendInlineKeyboardAsync(data.ShippingChatId,ShippingPackDeliveryNotice(data),buttons,ct);
        await RecordShippingPackUiAsync(row!.Id,sent,ct,control:true);
    }
    private async Task<(ShippingPackPhotoDraft Draft,ShippingPackApprovalData Approval)> LoadDeliveredShippingPackDraftAsync(Guid id,string chat,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Channel==ShippingPackDraftChannel&&!n.IsDeleted,ct)
            ??throw new InvalidOperationException("فرم عکس بسته پیدا نشد.");
        var draft=JsonSerializer.Deserialize<ShippingPackPhotoDraft>(row.Payload)!;
        if(row.Status!=NotificationOutboxStatus.Processed||draft.State!="queued"||row.CustomerId!=draft.Reference.CustomerId||row.Recipient!=chat||
            draft.Reference.ChatId!=chat||chat!=_options.ShippingChatId.Trim())throw new InvalidOperationException("این پاک‌سازی متعلق به همین فرم و گروه آماده‌سازی نیست.");
        var receipt=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==draft.ApprovalId&&n.Channel==ShippingPackApproval.ReceiptChannel&&!n.IsDeleted,ct)
            ??throw new InvalidOperationException("رسید تحویل عکس‌های این فرم پیدا نشد.");
        var approval=JsonSerializer.Deserialize<ShippingPackApprovalData>(receipt.Payload)!;
        if(receipt.CustomerId!=row.CustomerId||approval.CustomerId!=row.CustomerId||approval.PhotoDraftId!=id||approval.ShippingChatId!=chat||
            approval.PreviousApprovalId!=draft.PreviousApprovalId||approval.PreviousPhotoCount!=draft.ExistingPhotoCount||
            !(draft.Files??[]).SequenceEqual(approval.PhotoFileIds.Skip(draft.ExistingPhotoCount))||!ShippingPackPhotosDelivered(approval))
            throw new InvalidOperationException("ابتدا باید تحویل موفق عکس‌ها برای مشتری ثبت شود؛ فعلاً عکس‌های مسئول حفظ می‌شوند.");
        return(draft,approval);
    }
    private async Task HandleShippingPackCleanupAsync(TelegramCallbackQuery callback,Guid id,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id.ToString();
        var (draft,approval)=await LoadDeliveredShippingPackDraftAsync(id,chat,ct);
        var known=(draft.CleanupControlIds??[]).Contains(callback.Message.MessageId);
        var fast=callback.Message.From?.IsBot==true&&long.TryParse(_options.BotToken.Split(':')[0],out var bot)&&callback.Message.From.Id==bot&&
            callback.Message.Text?.StartsWith($"✅ {approval.PhotoFileIds.Length} عکس بسته برای مشتری ارسال شد.",StringComparison.Ordinal)==true;
        if(!known&&!fast)throw new InvalidOperationException("از دکمهٔ حذف پیام‌های اضافیِ همین فرم استفاده کنید.");
        // The callback itself proves the delivery of a fast-clicked cleanup panel.
        draft=await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with
        {CleanupRequested=true,UiMessageIds=(d.UiMessageIds??[]).Append(callback.Message.MessageId).Distinct().ToArray(),
            CleanupControlIds=(d.CleanupControlIds??[]).Append(callback.Message.MessageId).Distinct().ToArray()},ct);
        await _sender.AnswerCallbackAsync(callback.Id,"در حال حذف پیام‌های اضافی…",ct);
        if(!await DeleteShippingPackExtrasAsync(id,draft,ct))
            await _sender.EditTextWithKeyboardAsync(chat,callback.Message.MessageId,
                "حذف بعضی پیام‌ها انجام نشد؛ دوباره امتحان کنید. اگر عکس‌های مسئول باقی ماند، مجوز حذف پیام ربات در گروه بررسی شود.",
                [[new("🧹 حذف پیام‌های اضافی",ShippingReferencePrefix+"packclear:"+id.ToString("N"))]],ct);
    }
    private async Task<bool> DeleteShippingPackExtrasAsync(Guid id,ShippingPackPhotoDraft draft,CancellationToken ct)
    {
        var (_,approval)=await LoadDeliveredShippingPackDraftAsync(id,draft.Reference.ChatId,ct);
        var protectedIds=approval.Deliveries.Where(d=>d.ChatId==draft.Reference.ChatId&&d.MessageId.HasValue).Select(d=>d.MessageId!.Value)
            .Concat(draft.Reference.MessageId.HasValue?[draft.Reference.MessageId.Value]:[]).ToHashSet();
        long[] AllIds(ShippingPackPhotoDraft d)=>(d.PromptIds??[]).Concat(d.PromptId.HasValue?[d.PromptId.Value]:[])
            .Concat(d.PhotoMessages??[]).Concat(d.UiMessageIds??[]).Where(mid=>mid>0&&!protectedIds.Contains(mid)&&!(d.NavigationMessageIds??[]).Contains(mid)).Distinct().ToArray();
        var controls=(draft.CleanupControlIds??[]).ToHashSet();var failed=false;
        var navigation=await ShippingPackAddressNavigationAsync(draft.Reference,ct);
        foreach(var message in AllIds(draft).Except(draft.DeletedMessageIds??[]).OrderBy(mid=>controls.Contains(mid)?1:0))
        {
            if(failed&&controls.Contains(message))continue;
            if(controls.Contains(message)&&navigation.Length>0)
            {
                var edited=await _sender.EditTextWithKeyboardAsync(draft.Reference.ChatId,message,"✅ پیام‌های اضافی این بسته پاک شد.",navigation,ct);
                if(!edited.IsSuccessful){failed=true;continue;}
                await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with
                    {NavigationMessageIds=(d.NavigationMessageIds??[]).Append(message).Distinct().ToArray()},ct);
                continue;
            }
            var result=await _sender.DeleteMessageAsync(draft.Reference.ChatId,message,ct);
            if(!result.IsSuccessful&&result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true){failed=true;continue;}
            await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with
                {DeletedMessageIds=(d.DeletedMessageIds??[]).Append(message).Distinct().ToArray()},ct);
        }
        var complete=false;
        await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>
        {complete=!AllIds(d).Except(d.DeletedMessageIds??[]).Any();return d with{CleanupCompletedAt=complete?DateTime.UtcNow:null};},ct);
        return complete;
    }
}
