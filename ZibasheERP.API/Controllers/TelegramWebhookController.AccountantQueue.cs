using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal const string AccountantReplyChannel="AccountantReplacement";
    internal const string AccountantBacklogChannel="AccountantReplacementBacklog";
    private sealed record AccountantQuestion(string ChatId,string? ChatTitle,long MessageId,long UserId,string? Username,
        string Text,DateTime ReceivedAtUtc,DateTime? AttemptedAtUtc=null,long? ReplyMessageId=null,string? Outcome=null);

    private bool IsAccountantStaff(string? username,string? userId=null)=>
        NormalizeLearningUsername(username) is LearningAccountantUsername or LearningManagerUsername ||
        (!string.IsNullOrWhiteSpace(userId)&&userId==_options.OwnerUserId);

    private bool CanQueueAccountantQuestion(TelegramMessage message)=>
        message.MessageId>0 && message.Chat.Id<0 && message.From is {IsBot:false} && message.Chat.Type is "group" or "supergroup" &&
        !_options.IsSilentGroup(message.Chat) && !IsAccountantStaff(message.From.Username,message.From.Id.ToString(CultureInfo.InvariantCulture)) &&
        message.Text is {Length:>=2 and <=4000} text && !text.TrimStart().StartsWith('/') &&
        !ContainsSensitiveAccountantLearningData(text) && LooksLikeAccountantReplacementQuestion(text) &&
        !IsCustomerStatusQuestion(text) && !IsPerfumeGuidanceQuestion(text);

    private async Task<bool> QueueAccountantQuestionAsync(TelegramMessage message,DateTime receivedAt,CancellationToken ct,bool backlog=false)
    {
        if(!CanQueueAccountantQuestion(message))return false;
        var chat=message.Chat.Id.ToString(CultureInfo.InvariantCulture);
        if(!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==chat,ct))return false;
        var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"accountant-reply:{chat}:{message.MessageId}"))[..16]);
        var payload=JsonSerializer.Serialize(new AccountantQuestion(chat,message.Chat.Title,message.MessageId,message.From!.Id,message.From.Username,message.Text!,receivedAt));
        var empty=Guid.Empty;var channel=backlog?AccountantBacklogChannel:AccountantReplyChannel;
        return await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox(Id,CustomerId,Channel,EventType,Recipient,Payload,Status,Attempts,CreatedAt,IsDeleted)
            SELECT {id},{empty},{channel},{"AccountantQuestion"},{chat},{payload},{NotificationOutboxStatus.Pending},{0},{DateTime.UtcNow},{false}
            WHERE NOT EXISTS(SELECT 1 FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id});
            """,ct)>0;
    }

    internal async Task<int> QueueStoredAccountantQuestionsAsync(CancellationToken ct)
    {
        var since=DateTime.UtcNow.AddHours(-24);
        var activation=await _db.NotificationOutbox.AsNoTracking().Where(v=>!v.IsDeleted&&v.Channel=="AccountantReplyControl"&&v.EventType=="Activated")
            .OrderByDescending(v=>v.CreatedAt).Select(v=>v.Payload).FirstOrDefaultAsync(ct);
        if(activation is null)return 0; // Legacy replies have no receipt ledger; only explicit new activations scan history.
        if(activation is not null)
        {
            using var marker=JsonDocument.Parse(activation);
            if(marker.RootElement.TryGetProperty("SinceUtc",out var value)&&value.TryGetDateTime(out var stoppedAt)&&stoppedAt>since)since=stoppedAt;
        }
        var history=await _db.TelegramAccountantLearningMessages.AsNoTracking().Where(m=>!m.IsDeleted&&m.CreatedAt>=since&&
            _db.CustomerTelegramGroups.Any(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==m.ChatId)).OrderBy(m=>m.CreatedAt).ToArrayAsync(ct);
        var byChat=history.GroupBy(m=>m.ChatId).ToDictionary(g=>g.Key,g=>g.ToArray());var count=0;
        foreach(var row in history.Where(m=>!m.IsAccountant))
        {
            if(!long.TryParse(row.ChatId,out var chat)||!long.TryParse(row.SenderTelegramUserId,out var user))continue;
            if(byChat[row.ChatId].Any(a=>(a.IsAccountant||IsAccountantStaff(a.SenderUsername,a.SenderTelegramUserId))&&
                a.MessageId>row.MessageId&&(a.ReplyToMessageId==null||a.ReplyToMessageId==row.MessageId)))continue;
            var message=new TelegramMessage(row.MessageText,new(chat,"supergroup",row.ChatTitle),new(user,row.SenderUsername),null,MessageId:row.MessageId);
            if(await QueueAccountantQuestionAsync(message,row.CreatedAt,ct,true))count++;
        }
        return count;
    }

    private async Task ObserveAccountantHumanReplyAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||message.From.IsBot||message.MessageId<=0||
            !IsAccountantStaff(message.From.Username,message.From.Id.ToString(CultureInfo.InvariantCulture)))return;
        var text=FirstTelegramMessageText(message);
        if(text?.TrimStart().StartsWith('/')==true||(string.IsNullOrWhiteSpace(text)&&message.ReplyToMessage==null))return;
        var chat=message.Chat.Id.ToString(CultureInfo.InvariantCulture);
        if(!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==chat,ct))return;
        var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"accountant-human:{chat}:{message.MessageId}"))[..16]);
        var payload=JsonSerializer.Serialize(new{message.MessageId,ReplyToMessageId=message.ReplyToMessage?.MessageId});
        var empty=Guid.Empty;var now=DateTime.UtcNow;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO NotificationOutbox(Id,CustomerId,Channel,EventType,Recipient,Payload,Status,Attempts,CreatedAt,ProcessedAt,IsDeleted)
            SELECT {id},{empty},{"AccountantReplyControl"},{"HumanReply"},{chat},{payload},{NotificationOutboxStatus.Processed},{0},{now},{now},{false}
            WHERE NOT EXISTS(SELECT 1 FROM NotificationOutbox WITH(UPDLOCK,HOLDLOCK) WHERE Id={id});
            """,ct);
    }

    private async Task<bool> AccountantQuestionStillPending(AccountantQuestion question,CancellationToken ct)
    {
        if(question.ReceivedAtUtc<DateTime.UtcNow.AddHours(-24))return false;
        if(!await _db.CustomerTelegramGroups.AsNoTracking().AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==question.ChatId,ct))return false;
        var answers=await _db.TelegramAccountantLearningMessages.AsNoTracking().Where(m=>!m.IsDeleted&&m.ChatId==question.ChatId&&
            m.MessageId>question.MessageId&&(m.ReplyToMessageId==null||m.ReplyToMessageId==question.MessageId))
            .Select(m=>new{m.IsAccountant,m.SenderUsername,m.SenderTelegramUserId}).ToArrayAsync(ct);
        if(answers.Any(a=>a.IsAccountant||IsAccountantStaff(a.SenderUsername,a.SenderTelegramUserId)))return false;
        var markers=await _db.NotificationOutbox.AsNoTracking().Where(v=>!v.IsDeleted&&v.Channel=="AccountantReplyControl"&&v.EventType=="HumanReply"&&
            v.Recipient==question.ChatId&&v.CreatedAt>=question.ReceivedAtUtc).Select(v=>v.Payload).ToArrayAsync(ct);
        return !markers.Any(raw=>
        {
            using var marker=JsonDocument.Parse(raw);var data=marker.RootElement;
            return data.GetProperty("MessageId").GetInt64()>question.MessageId&&
                (data.GetProperty("ReplyToMessageId").ValueKind==JsonValueKind.Null||data.GetProperty("ReplyToMessageId").GetInt64()==question.MessageId);
        });
    }

    internal async Task ProcessAccountantQuestionAsync(NotificationOutbox job,CancellationToken ct)
    {
        if(job.Status is NotificationOutboxStatus.Processed or NotificationOutboxStatus.Failed)return;
        var question=JsonSerializer.Deserialize<AccountantQuestion>(job.Payload)??throw new InvalidOperationException("Invalid accountant reply job");
        if((job.Channel!=AccountantReplyChannel&&job.Channel!=AccountantBacklogChannel)||job.Recipient!=question.ChatId)throw new InvalidOperationException("Invalid accountant reply destination");
        if(question.AttemptedAtUtc!=null)
        {job.Status=NotificationOutboxStatus.Failed;job.LockedUntil=null;job.LastError="TelegramReplyOutcomeUnknown";await _db.SaveChangesAsync(ct);return;}
        if(!await IsAccountantReplacementReplyEnabledAsync(ct))
        {job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(5);await _db.SaveChangesAsync(ct);return;}
        if(!await AccountantQuestionStillPending(question,ct))
        {await Finish("already_handled_or_expired");return;}
        var message=new TelegramMessage(question.Text,new(long.Parse(question.ChatId),"supergroup",question.ChatTitle),new(question.UserId,question.Username),null,MessageId:question.MessageId);
        if(!CanQueueAccountantQuestion(message)){await Finish("not_eligible");return;}
        if(!_accountantReplacementService.IsEnabled)
        {job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.LastError="AssistantNotConfigured";job.NextAttemptAt=DateTime.UtcNow.AddMinutes(5);await _db.SaveChangesAsync(ct);return;}
        string? answer;
        try{answer=await GenerateAccountantReplacementAnswerAsync(message,ct);}
        catch(InvalidOperationException ex)when(ex.Message=="AccountantGenerationFailed")
        {
            job.Attempts++;job.LockedUntil=null;job.LastError="AccountantGenerationFailed";
            job.Status=job.Attempts<3?NotificationOutboxStatus.Pending:NotificationOutboxStatus.Failed;
            job.NextAttemptAt=DateTime.UtcNow.AddSeconds(30*job.Attempts);await _db.SaveChangesAsync(ct);return;
        }
        if(string.IsNullOrWhiteSpace(answer)){await Finish("no_confident_answer");return;}
        if(!await IsAccountantReplacementReplyEnabledAsync(ct)||!await AccountantQuestionStillPending(question,ct))
        {job.Status=NotificationOutboxStatus.Pending;job.LockedUntil=null;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(5);await _db.SaveChangesAsync(ct);return;}
        question=question with{AttemptedAtUtc=DateTime.UtcNow};job.Payload=JsonSerializer.Serialize(question);await _db.SaveChangesAsync(ct);
        var sent=await _sender.SendReplyOnceAsync(question.ChatId,answer,question.MessageId,ct);
        job.Attempts++;
        if(sent.IsSuccessful)
        {question=question with{ReplyMessageId=sent.MessageId};await Finish("answered");return;}
        job.LockedUntil=null;
        if(sent.IsRetryable&&job.Attempts<3)
        {
            question=question with{AttemptedAtUtc=null};job.Payload=JsonSerializer.Serialize(question);
            job.Status=NotificationOutboxStatus.Pending;job.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Max(30,sent.RetryAfterSeconds??60));job.LastError="TelegramReplyRejectedRetryable";
        }
        else {job.Status=NotificationOutboxStatus.Failed;job.LastError="TelegramReplyFailedNeedsReview";}
        await _db.SaveChangesAsync(ct);

        async Task Finish(string outcome)
        {
            job.Payload=JsonSerializer.Serialize(question with{Outcome=outcome});job.Status=NotificationOutboxStatus.Processed;
            job.ProcessedAt=DateTime.UtcNow;job.LockedUntil=null;job.LastError=null;job.NextAttemptAt=null;
            await _db.SaveChangesAsync(ct);
        }
    }
}
