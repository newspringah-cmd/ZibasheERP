using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private const string ShippingPackRevisionChannel="ShippingPackRevisionDraft";
    internal const string ShippingPackResponseChannel="ShippingPackCustomerResponse";
    private const string ShippingPackConfirmedText="تایید شد ✅️";
    private const string ShippingPackReviewSentText="برای بررسی مجدد ارسال شد";
    private const string ShippingRevisionPrompt="✏️ لطفاً توضیح بده کدام اقلام بسته نیاز به اصلاح دارد.\nروی همین پیام Reply بزن و توضیحاتت را بنویس؛ بعد از دیدن پیش‌نمایش، دکمهٔ «ارسال» را بزن.\nبرای انصراف، در پاسخ به همین پیام /cancel را بفرست.";
    private sealed record ShippingPackRevisionDraft(Guid ApprovalId,Guid CustomerId,long UserId,string ChatId,long PhotoMessageId,
        long? PromptId=null,long? PreviewId=null,string? Text=null,long? InputMessageId=null,string State="text");
    private sealed record ShippingPackResponseAttempt(DateTime? AttemptedAt=null,DateTime? SentAt=null,long? MessageId=null,string? Error=null);
    private sealed record ShippingPackResponseData(Guid ApprovalId,string Kind,string CustomerChatId,long PhotoMessageId,string? RevisionText,bool NotifyOperator,long ActorId,string? ActorUsername,
        ShippingPackResponseAttempt? Customer=null,ShippingPackResponseAttempt? Operator=null,bool StaffApproval=false);

    private async Task<bool> IsShippingPackApprovalActorAsync(Customer? customer,TelegramUser actor,string chat,bool allowStaff,CancellationToken ct)
    {
        if(customer is null||actor.IsBot||!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==customer.Id&&g.ChatId==chat,ct))return false;
        if(await IsShippingPackCustomerAsync(customer,actor,chat,ct))return true;
        return allowStaff&&(IsPrimaryOwner(actor.Id)||await IsAuthorizedInvoiceActionAdminAsync(actor.Id,ct));
    }
    private string ShippingPackStaffLabel(long actor,string? username)=>
        (IsPrimaryOwner(actor)?"مدیر":"ادمین")+": "+(string.IsNullOrWhiteSpace(username)?actor.ToString():"@"+username.Trim().TrimStart('@'));

    private async Task<bool> IsShippingPackCustomerAsync(Customer? customer,TelegramUser actor,string chat,CancellationToken ct)
    {
        if(customer is null||actor.IsBot||!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==customer.Id&&g.ChatId==chat,ct))return false;
        if(long.TryParse(customer.TelegramId,out var numeric)&&numeric>0)return numeric==actor.Id;
        var name=customer.Username?.Trim().TrimStart('@').ToLowerInvariant();
        if(string.IsNullOrEmpty(name)||name!=actor.Username?.Trim().TrimStart('@').ToLowerInvariant())return false;
        return !await _db.Customers.AsNoTracking().AnyAsync(c=>!c.IsDeleted&&c.Id!=customer.Id&&c.Username!=null&&c.Username.Trim().Replace("@", "").ToLower()==name,ct);
    }
    private async Task QueueShippingPackResponseAsync(Guid id,Guid approvalId,Guid customerId,string chat,long photo,string kind,string? text,bool notifyOperator,TelegramUser actor,CancellationToken ct,bool staffApproval=false)
    {
        _db.NotificationOutbox.Add(new(){Id=id,CustomerId=customerId,Channel=ShippingPackResponseChannel,EventType=kind,Recipient=chat,Status=NotificationOutboxStatus.Pending,
            Payload=JsonSerializer.Serialize(new ShippingPackResponseData(approvalId,kind,chat,photo,text,notifyOperator,actor.Id,actor.Username,StaffApproval:staffApproval)),CreatedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);
    }
    private async Task<(Guid Id,bool SendPrompt)> BeginShippingPackRevisionAsync(NotificationOutbox approvalRow,ShippingPackApprovalData data,TelegramCallbackQuery callback,CancellationToken ct)
    {
        // The caller holds the customer lock and has verified the actual photo,
        // recipient, latest proposal and current parcel before entering this flow.
        var locked=await _db.NotificationOutbox.FromSqlInterpolated($"SELECT * FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={approvalRow.Id}").AsNoTracking().SingleAsync(ct);
        var fresh=JsonSerializer.Deserialize<ShippingPackApprovalData>(locked.Payload)!;
        if(fresh.State==ShippingPackApprovalState.Dispatched)throw new InvalidOperationException("این بسته قبلاً ارسال شده است.");
        if(fresh.RevisionSubmittedAt.HasValue)throw new InvalidOperationException("توضیحات اصلاح قبلاً برای بررسی فرستاده شده؛ منتظر بررسی مسئول ارسال باشید.");
        if(fresh.RevisionDraftId.HasValue)
        {
            var existing=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==fresh.RevisionDraftId&&!n.IsDeleted&&n.Channel==ShippingPackRevisionChannel&&n.Status==NotificationOutboxStatus.Pending,ct);
            if(existing is not null)
            {
                var draft=JsonSerializer.Deserialize<ShippingPackRevisionDraft>(existing.Payload)!;
                if(draft.UserId==callback.From.Id&&draft.ChatId==callback.Message!.Chat.Id.ToString())return(existing.Id,!draft.PromptId.HasValue||draft.State=="ready"&&!draft.PreviewId.HasValue);
                await _db.NotificationOutbox.Where(n=>n.Id==existing.Id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed),ct);
            }
        }
        var id=Guid.NewGuid();var chat=callback.Message!.Chat.Id.ToString();
        var next=new ShippingPackRevisionDraft(approvalRow.Id,data.CustomerId,callback.From.Id,chat,callback.Message.MessageId);
        _db.NotificationOutbox.Add(new(){Id=id,CustomerId=data.CustomerId,Channel=ShippingPackRevisionChannel,EventType="AwaitingCorrection",Recipient=chat,
            Payload=JsonSerializer.Serialize(next),Status=NotificationOutboxStatus.Pending,CreatedAt=DateTime.UtcNow});
        await _db.SaveChangesAsync(ct);
        await MutateShippingPayloadAsync<ShippingPackApprovalData>(approvalRow.Id,ShippingPackApproval.ReceiptChannel,d=>d with
        {State=ShippingPackApprovalState.RevisionRequested,ConfirmedAt=null,ConfirmedBy=null,ConfirmationResponseId=null,RevisionDraftId=id,
            Deliveries=d.Deliveries.Select(p=>p.ChatId==chat&&p.Index==d.PhotoFileIds.Length-1?p with{SentAt=p.SentAt??DateTime.UtcNow,MessageId=callback.Message.MessageId}:p).ToArray()},ct);
        return(id,true);
    }
    private async Task SendShippingPackRevisionPromptAsync(Guid id,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id&&n.Channel==ShippingPackRevisionChannel,ct);
        var draft=JsonSerializer.Deserialize<ShippingPackRevisionDraft>(row.Payload)!;
        if(draft.State=="ready"&&draft.Text is not null){await SendShippingPackRevisionPreviewAsync(id,draft,ct);return;}
        var sent=await _sender.SendKeyboardDismissAsync(draft.ChatId,ShippingRevisionPrompt,ct);
        if(!sent.IsSuccessful||!sent.MessageId.HasValue)throw new InvalidOperationException("پیام دریافت توضیحات ارسال نشد؛ دوباره «نیاز به اصلاح» را بزنید.");
        await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(id,ShippingPackRevisionChannel,d=>d with{PromptId=sent.MessageId},ct);
    }
    private static string ShippingRevisionPreviewText(string text)=>"✏️ توضیحات اصلاح بسته:\n\n"+text+"\n\nبا زدن «ارسال»، این توضیحات برای مسئول آماده‌سازی فرستاده می‌شود.";
    private async Task SendShippingPackRevisionPreviewAsync(Guid id,ShippingPackRevisionDraft draft,CancellationToken ct)
    {
        IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>> buttons=
            [[new("📤 ارسال",ShippingReferencePrefix+"fixsend:"+id.ToString("N")),new("✏️ ویرایش توضیحات",ShippingReferencePrefix+"fixedit:"+id.ToString("N"))],
             [new("❌ انصراف",ShippingReferencePrefix+"fixcancel:"+id.ToString("N"))]];
        var preview=await _sender.SendInlineKeyboardAsync(draft.ChatId,ShippingRevisionPreviewText(draft.Text!),buttons,ct);
        if(!preview.IsSuccessful||!preview.MessageId.HasValue)throw new InvalidOperationException("پیش‌نمایش ارسال نشد؛ دوباره «نیاز به اصلاح» را بزنید.");
        await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(id,ShippingPackRevisionChannel,d=>d with{PreviewId=preview.MessageId},ct);
    }
    private async Task<(NotificationOutbox Row,ShippingPackRevisionDraft Draft,ShippingPackApprovalData Approval)> ValidateShippingRevisionAsync(Guid id,TelegramUser actor,string chat,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==ShippingPackRevisionChannel,ct)
            ??throw new InvalidOperationException("درخواست اصلاح پیدا نشد.");
        var draft=JsonSerializer.Deserialize<ShippingPackRevisionDraft>(row.Payload)!;
        var customer=await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==draft.CustomerId&&!c.IsDeleted,ct);
        if(row.CustomerId!=draft.CustomerId||row.Recipient!=chat||draft.ChatId!=chat||draft.UserId!=actor.Id||!await IsShippingPackCustomerAsync(customer,actor,chat,ct))
            throw new InvalidOperationException("این درخواست اصلاح متعلق به مشتری همین بسته و گروه متصل اوست.");
        var receipt=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==draft.ApprovalId&&n.Channel==ShippingPackApproval.ReceiptChannel&&!n.IsDeleted,ct);
        var approval=JsonSerializer.Deserialize<ShippingPackApprovalData>(receipt.Payload)!;
        var latest=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackApproval.ReceiptChannel&&n.CustomerId==draft.CustomerId&&n.Recipient==receipt.Recipient)
            .OrderByDescending(n=>n.CreatedAt).ThenByDescending(n=>n.Id).Select(n=>n.Id).FirstAsync(ct);
        if(approval.CustomerId!=draft.CustomerId||latest!=draft.ApprovalId||approval.RevisionDraftId!=id||approval.State!=ShippingPackApprovalState.RevisionRequested)
            throw new InvalidOperationException("این فرم دیگر فعال نیست؛ از آخرین عکس بسته استفاده کنید.");
        if(row.Status!=NotificationOutboxStatus.Pending||draft.State is "submitted" or "cancelled")throw new InvalidOperationException("این درخواست قبلاً ارسال یا بسته شده است.");
        return(row,draft,approval);
    }
    private async Task<bool> TryHandleShippingPackRevisionMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||string.IsNullOrWhiteSpace(message.Text)||message.ReplyToMessage?.From?.IsBot!=true)return false;
        if(!long.TryParse(_options.BotToken.Split(':')[0],out var sourceBot)||message.ReplyToMessage.From.Id!=sourceBot)return false;
        var chat=message.Chat.Id.ToString();
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingPackRevisionChannel&&n.Status==NotificationOutboxStatus.Pending&&n.Recipient==chat)
            .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
        var active=rows.Select(n=>(Row:n,Draft:JsonSerializer.Deserialize<ShippingPackRevisionDraft>(n.Payload)!)).FirstOrDefault(x=>x.Draft.UserId==message.From.Id&&
            (x.Draft.PromptId==message.ReplyToMessage.MessageId||!x.Draft.PromptId.HasValue&&message.ReplyToMessage.From?.IsBot==true&&
             long.TryParse(_options.BotToken.Split(':')[0],out var bot)&&message.ReplyToMessage.From.Id==bot&&message.ReplyToMessage.Text==ShippingRevisionPrompt&&
             message.ReplyToMessage.Date.HasValue&&DateTimeOffset.FromUnixTimeSeconds(message.ReplyToMessage.Date.Value).UtcDateTime>=x.Row.CreatedAt.AddSeconds(-2)));
        if(active.Row is null)return false;
        try
        {
            if(message.Text.Trim()=="/cancel")
            {await HandleShippingPackRevisionCallbackAsync(new("",message.From,message.ReplyToMessage,null),active.Row.Id,"fixcancel",ct);return true;}
            await using var tx=await _db.Database.BeginTransactionAsync(ct);
            await _db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={active.Draft.CustomerId}").AsNoTracking().SingleAsync(ct);
            var (_,draft,_)=await ValidateShippingRevisionAsync(active.Row.Id,message.From,chat,ct);
            if(draft.InputMessageId==message.MessageId)
            {await tx.CommitAsync(ct);if(!draft.PreviewId.HasValue)await SendShippingPackRevisionPreviewAsync(active.Row.Id,draft,ct);return true;}
            if(draft.State!="text")throw new InvalidOperationException("توضیحات ثبت شده؛ برای تغییر آن، «ویرایش توضیحات» را بزنید.");
            var text=message.Text.Trim();if(text.Length>1500)throw new InvalidOperationException("توضیحات اصلاح حداکثر ۱۵۰۰ کاراکتر باشد.");
            await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(active.Row.Id,ShippingPackRevisionChannel,d=>d with
                {Text=text,InputMessageId=message.MessageId,State="ready",PromptId=d.PromptId??message.ReplyToMessage.MessageId},ct);
            await tx.CommitAsync(ct);
            await SendShippingPackRevisionPreviewAsync(active.Row.Id,draft with{Text=text,State="ready"},ct);
        }
        catch(InvalidOperationException e){await _sender.SendReplyOnceAsync(chat,e.Message,message.MessageId,ct);}
        return true;
    }
    private async Task HandleShippingPackRevisionCallbackAsync(TelegramCallbackQuery callback,Guid id,string action,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id.ToString();
        var initial=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Channel==ShippingPackRevisionChannel&&!n.IsDeleted,ct)
            ??throw new InvalidOperationException("درخواست اصلاح پیدا نشد.");
        var first=JsonSerializer.Deserialize<ShippingPackRevisionDraft>(initial.Payload)!;
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await _db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={first.CustomerId}").AsNoTracking().SingleAsync(ct);
        var (_,draft,approval)=await ValidateShippingRevisionAsync(id,callback.From,chat,ct);
        var previewMatches=draft.PreviewId.HasValue?draft.PreviewId==callback.Message.MessageId:
            draft.State=="ready"&&callback.Message.Text==ShippingRevisionPreviewText(draft.Text!)&&callback.Message.From?.IsBot==true&&
            long.TryParse(_options.BotToken.Split(':')[0],out var botId)&&callback.Message.From.Id==botId;
        if(!previewMatches&&!(action=="fixcancel"&&draft.PromptId==callback.Message.MessageId))
            throw new InvalidOperationException("این دکمه متعلق به آخرین پیش‌نمایش توضیحات نیست.");
        if(action=="fixsend")
        {
            if(draft.State!="ready"||string.IsNullOrWhiteSpace(draft.Text))throw new InvalidOperationException("ابتدا توضیحات اصلاح را بنویسید.");
            await AssertShippingPackApprovalContentsAsync(approval,ct);
            var responseId=Guid.NewGuid();
            await MutateShippingPayloadAsync<ShippingPackApprovalData>(draft.ApprovalId,ShippingPackApproval.ReceiptChannel,d=>d with
                {RevisionText=draft.Text,RevisionSubmittedAt=DateTime.UtcNow,RevisionResponseId=responseId},ct);
            await QueueShippingPackResponseAsync(responseId,draft.ApprovalId,draft.CustomerId,chat,draft.PhotoMessageId,"revision",draft.Text,true,callback.From,ct);
            await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(id,ShippingPackRevisionChannel,d=>d with{State="submitted"},ct);
            await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed),ct);
        }
        else if(action=="fixedit")
        {
            await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(id,ShippingPackRevisionChannel,d=>d with{State="text",Text=null,InputMessageId=null,PreviewId=null},ct);
        }
        else
        {
            await MutateShippingPayloadAsync<ShippingPackRevisionDraft>(id,ShippingPackRevisionChannel,d=>d with{State="cancelled"},ct);
            await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Status,NotificationOutboxStatus.Processed),ct);
            await MutateShippingPayloadAsync<ShippingPackApprovalData>(draft.ApprovalId,ShippingPackApproval.ReceiptChannel,d=>d with
                {State=ShippingPackApprovalState.AwaitingCustomer,RevisionDraftId=null,ConfirmedAt=null,ConfirmedBy=null},ct);
        }
        await tx.CommitAsync(ct);
        await ApprovalResponseAsync(callback,action=="fixsend"?"توضیحات ثبت شد و در صف ارسال برای بررسی است.":action=="fixedit"?"توضیحات جدید را در پاسخ به پیام درخواست توضیح بفرستید.":"درخواست اصلاح لغو شد؛ برای ارسال بسته دوباره اقلام را تأیید کنید.",ct);
        await _sender.EditReplyMarkupAsync(chat,callback.Message.MessageId,[],ct);
        if(action!="fixedit"&&draft.PromptId.HasValue)await _sender.DeleteMessageAsync(chat,draft.PromptId.Value,ct);
    }
}
