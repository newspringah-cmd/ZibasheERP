using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string ShippingPackDraftChannel="ShippingPackPhotoDraft";
    private sealed record ShippingPackPhotoDraft(ShippingReferenceContext Reference,long UserId,Guid ApprovalId,
        long? PromptId=null,string PromptText="",string[]? Files=null,long[]? PhotoMessages=null,string? MediaGroup=null,string State="photos",long[]? PromptIds=null,long[]? UiMessageIds=null,long[]? QuestionIds=null,
        long? QuestionId=null,int QuestionPhotoCount=0,bool AwaitingMore=false,long[]? CleanupControlIds=null,long[]? DeletedMessageIds=null,
        bool CleanupRequested=false,DateTime? CleanupCompletedAt=null,long[]? NavigationMessageIds=null,
        Guid? PreviousApprovalId=null,int ExistingPhotoCount=0);

    private async Task<T> MutateShippingPayloadAsync<T>(Guid id,string channel,Func<T,T> mutate,CancellationToken ct)
    {
        await using var own=_db.Database.CurrentTransaction is null?await _db.Database.BeginTransactionAsync(ct):null;
        var row=await _db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id} AND Channel={channel} AND IsDeleted=0").AsNoTracking().SingleAsync(ct);
        var data=mutate(JsonSerializer.Deserialize<T>(row.Payload)!);var payload=JsonSerializer.Serialize(data);
        await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,payload).SetProperty(n=>n.UpdatedAt,DateTime.UtcNow),ct);
        if(own is not null)await own.CommitAsync(ct);return data;
    }
    private async Task StartShippingPackPhotoDraftAsync(TelegramCallbackQuery callback,ShippingReferenceContext context,OrderItem[] items,CancellationToken ct,bool replace=false)
    {
        _orderFlowDrafts.ClearShippingTrackingPhoto(callback.Message!.Chat.Id,callback.From.Id);
        _decantPhotoDrafts.Remove(callback.Message.Chat.Id,callback.From.Id);
        await CancelPendingShippingPackDraftsAsync(callback.Message.Chat.Id,callback.From.Id,ct);
        var previous=replace?null:await FindShippingPackAppendBaseAsync(context,items,ct);
        var id=Guid.NewGuid();var draft=new ShippingPackPhotoDraft(context with{ItemIds=items.Select(i=>i.Id).ToArray()},callback.From.Id,Guid.NewGuid(),
            PreviousApprovalId:previous?.Id,ExistingPhotoCount:previous?.Data.PhotoFileIds.Length??0);
        _db.NotificationOutbox.Add(new(){Id=id,CustomerId=context.CustomerId,Channel=ShippingPackDraftChannel,EventType="AwaitingPhotos",Recipient=context.ChatId,Status=NotificationOutboxStatus.Pending,Payload=JsonSerializer.Serialize(draft),CreatedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);await SendShippingPackPromptAsync(id,draft,ct);
    }
    private async Task CancelPendingShippingPackDraftsAsync(long chatId,long userId,CancellationToken ct)
    {
        var active=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackDraftChannel&&n.Status==NotificationOutboxStatus.Pending&&n.Recipient==chatId.ToString()).ToArrayAsync(ct);
        foreach(var row in active)
        {
            var draft=JsonSerializer.Deserialize<ShippingPackPhotoDraft>(row.Payload)!;
            if(draft.UserId==userId)await CancelShippingPackDraftAsync(row.Id,ct);
        }
    }
    private async Task CancelShippingPackDraftAsync(Guid id,CancellationToken ct)
    {
        await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d.State=="photos"?d with{State="cancelled"}:d,ct);
        await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed),ct);
    }
    private async Task CleanupClosedShippingPackPromptsAsync(Guid id,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id&&n.Channel==ShippingPackDraftChannel&&!n.IsDeleted,ct);
        var draft=JsonSerializer.Deserialize<ShippingPackPhotoDraft>(row.Payload)!;
        if(row.Status!=NotificationOutboxStatus.Processed||draft.State is not ("queued" or "cancelled"))return;
        var ids=(draft.PromptIds??[]).Concat(draft.PromptId.HasValue?[draft.PromptId.Value]:[]).Where(mid=>mid>0).Distinct();
        foreach(var messageId in ids)
        {
            var result=await _sender.DeleteMessageAsync(draft.Reference.ChatId,messageId,ct);
            if(!result.IsSuccessful&&result.Error?.Contains("message to delete not found",StringComparison.OrdinalIgnoreCase)!=true)
                _logger.LogWarning("Could not remove closed shipping pack prompt {PromptId}",messageId);
        }
    }
    private async Task SendShippingPackPromptAsync(Guid id,ShippingPackPhotoDraft draft,CancellationToken ct)
    {
        var customer=await _db.Customers.AsNoTracking().SingleAsync(c=>c.Id==draft.Reference.CustomerId,ct);
        var scope=draft.Reference.LegacyAddressOnly?"اقلام ثبت‌شدهٔ قبلی":$"{draft.Reference.ItemIds.Length} آیتم";
        var prompt=$"📷 بستهٔ {OrderCustomerLabel(customer)} — {scope}\nعکس {draft.ExistingPhotoCount+(draft.Files?.Length??0)+1} را همین‌جا بفرستید؛ نیازی به ریپلای نیست. در هر نوبت تا ۳ عکس می‌توانید اضافه کنید؛ بعد از دریافت عکس، به سؤال «آیا عکس دیگری دارید؟» پاسخ دهید."+
            (draft.ExistingPhotoCount>0?$"\n{draft.ExistingPhotoCount} عکس قبلی محفوظ است؛ عکس جدید در ادامهٔ همان عکس‌ها ارسال می‌شود.":"");
        await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with{PromptIds=(d.PromptIds??[]).Concat(d.PromptId.HasValue?[d.PromptId.Value]:[]).Distinct().ToArray(),PromptId=null,PromptText=prompt,MediaGroup=null},ct);
        var sent=await _sender.SendKeyboardDismissAsync(draft.Reference.ChatId,prompt,ct);
        if(!sent.IsSuccessful||!sent.MessageId.HasValue)throw new InvalidOperationException("پیام دریافت عکس ارسال نشد؛ دوباره از دکمهٔ عکس تأیید استفاده کنید.");
        await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with{PromptId=sent.MessageId,PromptIds=(d.PromptIds??[]).Append(sent.MessageId!.Value).Distinct().ToArray()},ct);
    }
    private async Task<bool> TryHandleShippingPackPhotoMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null)return false;
        if(message.ReplyToMessage is not null&&message.Text is not null)
        {
            var normalized=message.Text.Trim().Replace("أ","ا").Replace("ي","ی").Replace("ك","ک").Replace("تأیید","تایید");
            if(normalized is "تایید" or "تایید شد" or "تایید میکنم" or "تایید می‌کنم")
            {
                var linked=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==message.Chat.Id.ToString()).Select(g=>g.CustomerId).ToArrayAsync(ct);
                var receipts=await _db.NotificationOutbox.AsNoTracking().Where(n=>n.Channel==ShippingPackApproval.ReceiptChannel&&!n.IsDeleted&&linked.Contains(n.CustomerId)).OrderByDescending(n=>n.CreatedAt).Take(100).ToArrayAsync(ct);
                foreach(var receipt in receipts)
                {
                    var data=JsonSerializer.Deserialize<ShippingPackApprovalData>(receipt.Payload)!;
                    if(data.Deliveries.Any(d=>d.ChatId==message.Chat.Id.ToString()&&d.Index==data.PhotoFileIds.Length-1&&d.MessageId==message.ReplyToMessage.MessageId))
                    {
                        try{await HandleShippingPackCustomerApprovalAsync(new("",message.From,message.ReplyToMessage,null),receipt.Id,true,ct);}
                        catch(InvalidOperationException e){await ReplyAsync(message.Chat.Id,e.Message,ct);}
                        return true;
                    }
                }
            }
        }
        // Tracking and prepared-parcel photos are distinct input modes. The
        // explicitly selected tracking flow owns its next photo, even if an
        // older durable pack draft survived or a webhook overlapped the switch.
        if(_orderFlowDrafts.TryGetShippingTrackingPhoto(message.Chat.Id,message.From.Id,out _))
        {await CancelPendingShippingPackDraftsAsync(message.Chat.Id,message.From.Id,ct);return false;}
        var drafts=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackDraftChannel&&n.Status==NotificationOutboxStatus.Pending&&n.Recipient==message.Chat.Id.ToString())
            .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
        var own=drafts.Select(n=>(Row:n,Data:JsonSerializer.Deserialize<ShippingPackPhotoDraft>(n.Payload)!))
            .Where(x=>x.Data.UserId==message.From.Id&&x.Data.State=="photos"&&x.Data.Reference.ChatId==x.Row.Recipient&&x.Data.Reference.CustomerId==x.Row.CustomerId).ToArray();
        var active=own.FirstOrDefault(x=>x.Data.PromptId.HasValue&&message.ReplyToMessage?.MessageId==x.Data.PromptId);
        if(active.Row is null)active=own.FirstOrDefault();
        if(active.Row is null)return false;
        if(message.Text?.Trim()=="/cancel")
        {await CancelShippingPackDraftAsync(active.Row.Id,ct);await _sender.SendKeyboardDismissAsync(message.Chat.Id.ToString(),"دریافت عکس بسته لغو شد؛ عکسی برای مشتری ارسال نشد.",ct);await CleanupClosedShippingPackPromptsAsync(active.Row.Id,ct);return true;}
        if(message.Photo is not{Count:>0})return false;
        if(!await IsAuthorizedShippingAdminAsync(message.Chat.Id,message.From.Id,ct))return true;
        var exact=message.ReplyToMessage?.MessageId==active.Data.PromptId&&active.Data.PromptId.HasValue;
        var album=!string.IsNullOrEmpty(message.MediaGroupId)&&message.MediaGroupId==active.Data.MediaGroup;
        var quickReply=active.Data.PromptId is null&&message.ReplyToMessage?.From?.IsBot==true&&
            long.TryParse(_options.BotToken.Split(':')[0],out var botId)&&message.ReplyToMessage.From.Id==botId&&
            message.ReplyToMessage.Text==active.Data.PromptText&&message.ReplyToMessage.Date.HasValue&&
            DateTimeOffset.FromUnixTimeSeconds(message.ReplyToMessage.Date.Value).UtcDateTime>=active.Row.CreatedAt.AddSeconds(-2);
        // A normal upload belongs to the only open form for this operator in
        // this group. An explicit reply to another message must never fall
        // back to that form; neither may an old update or an ambiguous draft.
        var direct=message.ReplyToMessage is null&&own.Length==1&&active.Data.PromptId.HasValue&&
            (!message.Date.HasValue||DateTimeOffset.FromUnixTimeSeconds(message.Date.Value).UtcDateTime>=active.Row.CreatedAt.AddSeconds(-2));
        if(!exact&&!album&&!quickReply&&!direct)
        {
            var warning=await _sender.SendKeyboardDismissAsync(message.Chat.Id.ToString(),"برای افزودن عکس بسته، روی پیام درخواست عکسِ همین بسته ریپلای کنید.",ct);
            await RecordShippingPackUiAsync(active.Row.Id,warning,ct);return true;
        }
        var file=message.Photo.OrderByDescending(p=>p.Width*p.Height).First().FileId;
        ShippingPackPhotoDraft saved;var added=false;var overLimit=false;
        try
        {
            saved=await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(active.Row.Id,ShippingPackDraftChannel,d=>
            {
                if(d.State!="photos")throw new InvalidOperationException("این درخواست قبلاً ارسال یا بسته شده است.");
                if(direct&&d.PromptId!=active.Data.PromptId)throw new InvalidOperationException("مرحلهٔ دریافت عکس تغییر کرده؛ عکس را دوباره برای فرم فعلی بفرستید.");
                var files=d.Files??[];var ids=d.PhotoMessages??[];
                if(ids.Contains(message.MessageId))return d;
                if(files.Contains(file))return d with{PhotoMessages=[..ids,message.MessageId]};
                if(files.Length>=3){overLimit=true;return d with{PhotoMessages=[..ids,message.MessageId]};}
                added=true;
                return d with{Files=[..files,file],PhotoMessages=[..ids,message.MessageId],MediaGroup=message.MediaGroupId??d.MediaGroup,AwaitingMore=true};
            },ct);
        }
        catch(InvalidOperationException e)
        {
            var warning=await _sender.SendKeyboardDismissAsync(message.Chat.Id.ToString(),e.Message,ct);
            await RecordShippingPackUiAsync(active.Row.Id,warning,ct);return true;
        }
        if(overLimit)
        {
            var warning=await _sender.SendKeyboardDismissAsync(message.Chat.Id.ToString(),"حداکثر ۳ عکس ثبت می‌شود؛ برای ارسال عکس‌های ثبت‌شده، «خیر» را بزنید.",ct);
            await RecordShippingPackUiAsync(active.Row.Id,warning,ct);return true;
        }
        if(added||saved.AwaitingMore&&saved.QuestionPhotoCount!=(saved.Files?.Length??0))await SendShippingPackMoreQuestionAsync(active.Row.Id,saved,ct);
        return true;
    }
    private async Task HandleShippingPackDraftCallbackAsync(TelegramCallbackQuery callback,Guid id,string action,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Channel==ShippingPackDraftChannel&&!n.IsDeleted,ct)
            ??throw new InvalidOperationException("درخواست عکس پیدا نشد.");
        var draft=JsonSerializer.Deserialize<ShippingPackPhotoDraft>(row.Payload)!;
        if(draft.UserId!=callback.From.Id||draft.Reference.ChatId!=callback.Message!.Chat.Id.ToString())throw new InvalidOperationException("این درخواست عکس متعلق به شما نیست.");
        if(row.Status!=NotificationOutboxStatus.Pending||draft.State!="photos")throw new InvalidOperationException("این درخواست قبلاً در صف ارسال ثبت یا بسته شده است؛ ارسال تکراری انجام نمی‌شود.");
        if(action is "add" or "send")
        {
            var known=draft.QuestionIds??[];
            var valid=known.Contains(callback.Message.MessageId);
            var fast=callback.Message.From?.IsBot==true&&long.TryParse(_options.BotToken.Split(':')[0],out var sourceBot)&&callback.Message.From.Id==sourceBot&&
                callback.Message.Text?.Contains("آیا عکس دیگری دارید؟",StringComparison.Ordinal)==true;
            if(known.Length>0&&!valid&&!fast)throw new InvalidOperationException("از سؤال همین فرم برای انتخاب بله یا خیر استفاده کنید.");
            if(!valid&&fast)
                draft=await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>d with
                {UiMessageIds=(d.UiMessageIds??[]).Append(callback.Message.MessageId).Distinct().ToArray(),
                    QuestionIds=(d.QuestionIds??[]).Append(callback.Message.MessageId).Distinct().ToArray()},ct);
            if(action=="add"&&draft.QuestionId.HasValue&&draft.QuestionId!=callback.Message.MessageId)
                throw new InvalidOperationException("برای افزودن عکس از آخرین سؤال این فرم استفاده کنید.");
        }
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        if(action=="cancel")
        {await CancelShippingPackDraftAsync(id,ct);await _sender.SendKeyboardDismissAsync(callback.Message.Chat.Id.ToString(),"ارسال عکس لغو شد.",ct);await CleanupClosedShippingPackPromptsAsync(id,ct);return;}
        if(action=="add")
        {
            if((draft.Files?.Length??0)>=3)throw new InvalidOperationException("۳ عکس ثبت شده است؛ برای ارسال «خیر» را بزنید.");
            var advance=false;
            var next=await MutateShippingPayloadAsync<ShippingPackPhotoDraft>(id,ShippingPackDraftChannel,d=>
            {
                if(d.State!="photos")throw new InvalidOperationException("این فرم قبلاً ارسال یا بسته شده است.");
                if(!d.AwaitingMore&&(d.QuestionIds?.Length??0)>0)return d;
                advance=true;return d with{AwaitingMore=false};
            },ct);
            if(!advance)return;
            await _sender.EditReplyMarkupAsync(draft.Reference.ChatId,callback.Message.MessageId,[],ct);
            await SendShippingPackPromptAsync(id,next,ct);return;
        }
        if(draft.Reference.LegacyAddressOnly)await AssertLegacyPackAddressAsync(draft.Reference,ct);
        else
        {
            var items=await LoadShippingReferenceItemsAsync(draft.Reference,ct);
            if(items.Length==0||!items.Select(i=>i.Id).ToHashSet().SetEquals(draft.Reference.ItemIds))throw new InvalidOperationException("اقلام آمادهٔ بسته تغییر کرده؛ از دکمهٔ آدرس، عکس‌های بستهٔ جدید را ثبت کنید.");
        }
        if(draft.Files is not{Length:>=1 and <=3})throw new InvalidOperationException("ابتدا یک تا سه عکس ثبت کنید.");
        var groups=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==draft.Reference.CustomerId).Select(g=>g.ChatId).Distinct().ToArrayAsync(ct);
        if(groups.Length==0)throw new InvalidOperationException("گروه مشتری متصل نیست؛ ابتدا اتصال گروه را انجام دهید. عکس‌ها محفوظ هستند.");
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await _db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={draft.Reference.CustomerId}").AsNoTracking().SingleAsync(ct);
        var locked=await _db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id}").AsNoTracking().SingleAsync(ct);
        var fresh=JsonSerializer.Deserialize<ShippingPackPhotoDraft>(locked.Payload)!;
        if(locked.Status!=NotificationOutboxStatus.Pending||fresh.State!="photos"){await tx.RollbackAsync(ct);return;}
        ShippingPackApprovalData? previous=null;
        if(fresh.PreviousApprovalId.HasValue)
        {
            var candidate=await FindShippingPackAppendBaseAsync(fresh.Reference,await LoadShippingReferenceItemsAsync(fresh.Reference,ct),ct);
            if(candidate?.Id!=fresh.PreviousApprovalId||candidate.Data.PhotoFileIds.Length!=fresh.ExistingPhotoCount)
                throw new InvalidOperationException("عکس‌ها یا وضعیت بسته بعد از شروع فرم تغییر کرده؛ از دکمهٔ آدرس دوباره شروع کنید.");
            previous=candidate.Data;
            if(fresh.Files!.Any(file=>previous.PhotoFileIds.Contains(file)))throw new InvalidOperationException("این عکس قبلاً برای همین بسته ارسال شده؛ عکس جدیدی بفرستید.");
        }
        var approval=new ShippingPackApprovalData{CustomerId=fresh.Reference.CustomerId,PhotoDraftId=id,RequestId=fresh.Reference.RequestId,ItemIds=fresh.Reference.ItemIds,
            LegacyAddressOnly=fresh.Reference.LegacyAddressOnly,AddressId=fresh.Reference.AddressId,AddressMessageId=fresh.Reference.MessageId,
            PhotoFileIds=[..(previous?.PhotoFileIds??[]),..fresh.Files!],Deliveries=previous?.Deliveries??[],PreviousApprovalId=fresh.PreviousApprovalId,PreviousPhotoCount=fresh.ExistingPhotoCount,
            ShippingChatId=fresh.Reference.ChatId,OperatorUserId=fresh.UserId,State=ShippingPackApprovalState.Sending};
        _db.NotificationOutbox.Add(new(){Id=fresh.ApprovalId,CustomerId=approval.CustomerId,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=ShippingPackApprovalScope(approval),
            Payload=JsonSerializer.Serialize(approval),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});
        _db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),CustomerId=approval.CustomerId,Channel=ShippingPackApproval.SendChannel,EventType="SendPackPhotos",Recipient=fresh.Reference.ChatId,
            Payload=JsonSerializer.Serialize(new{ApprovalId=fresh.ApprovalId}),Status=NotificationOutboxStatus.Pending,CreatedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);
        await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed)
            .SetProperty(n=>n.Payload,JsonSerializer.Serialize(fresh with{State="queued",AwaitingMore=false},(JsonSerializerOptions?)null)),ct);
        await tx.CommitAsync(ct);
        var queuedNotice=await _sender.SendKeyboardDismissAsync(callback.Message.Chat.Id.ToString(),"✅ عکس‌ها در صف ارسال برای مشتری ثبت شدند. نتیجه و تأیید مشتری در همین گروه اعلام می‌شود.",ct);
        await RecordShippingPackUiAsync(id,queuedNotice,ct);
    }
    private async Task ApprovalResponseAsync(TelegramCallbackQuery callback,string text,CancellationToken ct)
    {if(callback.Id.Length==0)await ReplyAsync(callback.Message!.Chat.Id,text,ct);else await _sender.AnswerCallbackAsync(callback.Id,text,ct,true);}
    private async Task HandleShippingPackCustomerApprovalAsync(TelegramCallbackQuery callback,Guid id,bool approve,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel,ct);
        if(row is null){await ApprovalResponseAsync(callback,"درخواست تأیید پیدا نشد.",ct);return;}
        var data=JsonSerializer.Deserialize<ShippingPackApprovalData>(row.Payload)!;
        var customer=await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==data.CustomerId&&!c.IsDeleted,ct);
        var chat=callback.Message!.Chat.Id.ToString();var actorId=callback.From.Id.ToString();
        var identity=await IsShippingPackCustomerAsync(customer,callback.From,chat,ct);
        var authorized=identity||await IsShippingPackApprovalActorAsync(customer,callback.From,chat,approve,ct);
        var delivered=data.Deliveries.SingleOrDefault(d=>d.ChatId==chat&&d.Index==data.PhotoFileIds.Length-1);
        if(!authorized||callback.From.IsBot||!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==data.CustomerId&&g.ChatId==chat,ct)||
            delivered is null||delivered.AttemptedAt is null||delivered.MessageId.HasValue&&delivered.MessageId!=callback.Message.MessageId||
            data.Deliveries.Count(d=>d.ChatId==chat&&d.Index<data.PhotoFileIds.Length-1&&d.SentAt.HasValue)!=data.PhotoFileIds.Length-1)
        {await ApprovalResponseAsync(callback,approve?"تأیید فقط توسط مشتری همین بسته یا مدیر و ادمین مجاز، در گروه متصل همین مشتری مجاز است.":"درخواست اصلاح فقط توسط مشتری همین بسته و در گروه متصل خودش مجاز است.",ct);return;}
        if(data.State==ShippingPackApprovalState.Dispatched){await ApprovalResponseAsync(callback,"این بسته قبلاً ارسال شده است.",ct);return;}
        try
        {
            await AssertShippingPackApprovalContentsAsync(data,ct);
        }
        catch(InvalidOperationException e){await ApprovalResponseAsync(callback,e.Message,ct);return;}
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await _db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={data.CustomerId}").AsNoTracking().SingleAsync(ct);
        var latest=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==data.CustomerId&&n.Recipient==row.Recipient)
            .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Id).FirstAsync(ct);
        if(latest!=id){await tx.RollbackAsync(ct);await ApprovalResponseAsync(callback,"برای این بسته عکس جدیدتری ثبت شده؛ لطفاً آخرین پیام را تأیید کنید.",ct);return;}
        if(!approve)
        {
            var revision=await BeginShippingPackRevisionAsync(row,data,callback,ct);
            await tx.CommitAsync(ct);
            await ApprovalResponseAsync(callback,"توضیحات اصلاح را در پاسخ به پیام درخواست توضیح بفرستید؛ سپس «ارسال» را بزنید.",ct);
            if(revision.SendPrompt)await SendShippingPackRevisionPromptAsync(revision.Id,ct);
            return;
        }
        var won=false;var queue=false;var responseId=Guid.NewGuid();
        await MutateShippingPayloadAsync<ShippingPackApprovalData>(id,ShippingPackApproval.ReceiptChannel,d=>
        {
            if(d.State==ShippingPackApprovalState.RevisionRequested)throw new InvalidOperationException("این بسته نیازمند اصلاح است؛ پس از بررسی مسئول ارسال و دریافت عکس جدید آن را تأیید کنید.");
            if(d.State==ShippingPackApprovalState.Dispatched)throw new InvalidOperationException("این بسته قبلاً ارسال شده است.");
            won=!d.ConfirmedAt.HasValue;queue=!d.ConfirmationResponseId.HasValue;
            var delivery=d.Deliveries.Select(p=>p.ChatId==chat&&p.Index==d.PhotoFileIds.Length-1?p with{SentAt=p.SentAt??DateTime.UtcNow,MessageId=callback.Message.MessageId}:p).ToArray();
            return d with{State=ShippingPackApprovalState.Confirmed,ConfirmedAt=d.ConfirmedAt??DateTime.UtcNow,ConfirmedBy=d.ConfirmedBy??callback.From.Id,
                Deliveries=delivery,ConfirmationResponseId=d.ConfirmationResponseId??responseId};
        },ct);
        if(queue)await QueueShippingPackResponseAsync(responseId,id,data.CustomerId,chat,callback.Message.MessageId,"confirmed",null,won,callback.From,ct,!identity);
        await tx.CommitAsync(ct);
        await ApprovalResponseAsync(callback,"✅ تأیید شما ثبت شد؛ ممنون عزیزم ❤️",ct);
    }
    private async Task SendPreviousShippingPackPhotosAsync(long chat,ShippingReferenceContext context,CancellationToken ct)
    {
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==context.CustomerId)
            .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
        var previous=rows.Select(n=>JsonSerializer.Deserialize<ShippingPackApprovalData>(n.Payload)!).Where(d=>d.DispatchedAt.HasValue&&
            !(context.RequestId.HasValue&&d.RequestId==context.RequestId)&&!d.ItemIds.ToHashSet().SetEquals(context.ItemIds))
            .OrderByDescending(d=>d.DispatchedAt).FirstOrDefault();
        if(previous is null){await ShippingReferenceTextAsync(chat,"هنوز عکس بستهٔ پستی قبلی از طریق این بخش ثبت نشده است.",ct);return;}
        var files=previous.Deliveries.Where(d=>d.SentAt.HasValue).Select(d=>previous.PhotoFileIds[d.Index]).Distinct().ToArray();
        for(var index=0;index<files.Length;index++)await ShippingReferencePhotoAsync(chat.ToString(),files[index],$"🖼 عکس بستهٔ پستی قبلی — {index+1} از {files.Length}\nتاریخ ارسال: {FormatShippingPersianDate(previous.DispatchedAt!.Value)}",ct);
    }
}
