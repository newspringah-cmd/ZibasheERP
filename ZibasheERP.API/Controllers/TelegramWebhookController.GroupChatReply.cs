using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    internal const string GroupChatReplyChannel="AdminGroupChatReply";
    private const string GroupChatReplyPrefix="groupchatreply:";
    private sealed record GroupChatReplyDraft(string Step,DateTime Until,long Target=0,string? Text=null,long Source=0,string? Token=null,string? TargetChat=null);
    private sealed record GroupChatReplyJob(string TargetChat,long TargetMessage,string Text,long Actor,long AdminChat,long SourceMessage,
        string State="queued",DateTime? AttemptedAt=null,long? SentMessage=null,string? Error=null,bool NoticeAttempted=false);
    private static readonly ConcurrentDictionary<(long Chat,long User),GroupChatReplyDraft> GroupChatReplyDrafts=new();
    private Task GroupChatReplyPrompt(long chat,string text,CancellationToken ct)=>_sender.SendInlineKeyboardAsync(chat.ToString(),text,
        [[new("❌ انصراف",GroupChatReplyPrefix+"cancel")]],ct);

    private async Task<bool> TryHandleGroupChatReplyCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Message is null||callback.Data?.StartsWith(GroupChatReplyPrefix,StringComparison.Ordinal)!=true)return false;
        var chat=callback.Message.Chat.Id;var actor=callback.From.Id;var key=(chat,actor);var action=callback.Data[GroupChatReplyPrefix.Length..];
        if(callback.Message.Chat.Type!="private"||chat!=actor||!await IsAuthorizedInvoiceActionAdminAsync(actor,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"این گزینه فقط برای ادمین‌های مجاز در خصوصی ربات است.",ct,true);return true;}
        await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
        try
        {
            if(action=="start")
            {
                ClearAdminWorkflowDrafts(chat,actor);GroupChatReplyDrafts[key]=new("link",DateTime.UtcNow.AddMinutes(20));
                await GroupChatReplyPrompt(chat,"💬 پاسخگویی گروه چت\nلینک پیام موردنظر در گروه Zibashe perfume channel Chat را بفرستید.\nروی پیام موردنظر «کپی لینک» بزنید و لینک را اینجا بفرستید.\nپاسخ فقط به همان پیام در همین گروه ارسال می‌شود.",ct);return true;
            }
            if(action=="cancel")
            {GroupChatReplyDrafts.TryRemove(key,out _);await _sender.SendInlineKeyboardAsync(chat.ToString(),"فرایند پاسخگویی بسته شد.",[[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);return true;}
            if(action.StartsWith("status:")&&Guid.TryParseExact(action[7..],"N",out var statusId))
            {await ShowGroupChatReplyStatusAsync(chat,actor,statusId,ct);return true;}
            if(!GroupChatReplyDrafts.TryGetValue(key,out var draft)||draft.Until<DateTime.UtcNow||draft.Step!="text"||
                draft.Text is null||action!="send:"+draft.Token||draft.TargetChat!=_options.SalesDiscussionChatId.Trim()||!GroupChatReplyLink.TryParse(GroupChatReplyLink.Url(draft.TargetChat??"",draft.Target),_options.SalesDiscussionChatId.Trim(),out _))
                throw new InvalidOperationException("پیش‌نمایش منقضی یا جایگزین شده؛ دوباره از منو شروع کنید.");
            var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"admin-chat-reply:{chat}:{actor}:{draft.Source}:{draft.Target}"))[..16]);
            await using(var tx=await _db.Database.BeginTransactionAsync(ct))
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"admin-chat-reply:"+id},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Chat reply busy',1;",ct);
                if(!await _db.NotificationOutbox.AnyAsync(n=>n.Id==id,ct))
                {
                    var data=new GroupChatReplyJob(draft.TargetChat!,draft.Target,draft.Text,actor,chat,draft.Source);
                    _db.NotificationOutbox.Add(new(){Id=id,CustomerId=Guid.Empty,Channel=GroupChatReplyChannel,EventType="ReplyToDiscussionMessage",
                        Recipient=chat.ToString(),Payload=JsonSerializer.Serialize(data),CreatedAt=DateTime.UtcNow});
                    await _db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            ((ICollection<KeyValuePair<(long Chat,long User),GroupChatReplyDraft>>)GroupChatReplyDrafts).Remove(new(key,draft));
            await _sender.SendInlineKeyboardAsync(chat.ToString(),"✅ پاسخ تأیید شد و در صف ارسال است. نتیجه و لینک پاسخ همین‌جا اعلام می‌شود.",
                [[new("🔄 مشاهدهٔ نتیجه",GroupChatReplyPrefix+"status:"+id.ToString("N"))],[new("↩️ منوی مدیریت","invoiceadmin:menu:main")]],ct);
        }
        catch(InvalidOperationException e){await GroupChatReplyPrompt(chat,e.Message,ct);}
        return true;
    }

    private async Task<bool> TryHandleGroupChatReplyMessageAsync(TelegramMessage message,CancellationToken ct)
    {
        if(message.From is null||!GroupChatReplyDrafts.TryGetValue((message.Chat.Id,message.From.Id),out var draft))return false;
        var key=(message.Chat.Id,message.From.Id);var text=(message.Text??"").Trim();
        if(message.Chat.Type!="private"||message.Chat.Id!=message.From.Id||!await IsAuthorizedInvoiceActionAdminAsync(message.From.Id,ct)||text.StartsWith('/'))
        {GroupChatReplyDrafts.TryRemove(key,out _);return false;}
        if(text is "لغو" or "انصراف"){GroupChatReplyDrafts.TryRemove(key,out _);await ReplyAsync(message.Chat.Id,"فرایند پاسخگویی لغو شد.",ct);return true;}
        if(draft.Until<DateTime.UtcNow){GroupChatReplyDrafts.TryRemove(key,out _);await GroupChatReplyPrompt(message.Chat.Id,"فرایند منقضی شد؛ دوباره از منو شروع کنید.",ct);return true;}
        if(draft.Step=="link")
        {
            if(!GroupChatReplyLink.TryParse(text,_options.SalesDiscussionChatId.Trim(),out var target))
            {await GroupChatReplyPrompt(message.Chat.Id,"لینک معتبرِ پیام همین گروه را به شکل https://t.me/c/شناسه‌گروه/شماره‌پیام بفرستید؛ لینک کانال یا گروه دیگر پذیرفته نمی‌شود.",ct);return true;}
            GroupChatReplyDrafts[key]=draft with{Step="text",Target=target,TargetChat=_options.SalesDiscussionChatId.Trim()};
            await GroupChatReplyPrompt(message.Chat.Id,"متن پاسخ زیباشی را بفرستید؛ حداکثر ۴۰۹۶ نویسه. پیش از ارسال، متن و لینک مقصد را برای تأیید می‌بینید.",ct);return true;
        }
        if(string.IsNullOrWhiteSpace(message.Text)||message.Text.Length>4096)
        {await GroupChatReplyPrompt(message.Chat.Id,"یک پاسخ متنی با حداکثر ۴۰۹۶ نویسه بفرستید؛ در این گزینه عکس و فایل ارسال نمی‌شود.",ct);return true;}
        var token=Guid.NewGuid().ToString("N")[..12];
        GroupChatReplyDrafts[key]=draft with{Text=message.Text,Source=message.MessageId,Token=token,Until=DateTime.UtcNow.AddMinutes(20)};
        await _sender.SendAsync(message.Chat.Id.ToString(),"👁 پیش‌نمایش پاسخ\nپیام مقصد:\n"+GroupChatReplyLink.Url(draft.TargetChat??"",draft.Target)+"\nپاسخ زیر تا تأیید شما ارسال نمی‌شود. برای اصلاح، متن تازه بفرستید.",ct);
        await _sender.SendInlineKeyboardAsync(message.Chat.Id.ToString(),message.Text,
            [[new("✅ تأیید و ارسال پاسخ",GroupChatReplyPrefix+"send:"+token)],[new("❌ انصراف",GroupChatReplyPrefix+"cancel")]],ct);
        return true;
    }

    private static string GroupChatReplyStatusText(GroupChatReplyJob data)=>data.State switch {
        "sent"=>$"✅ پاسخ روی پیام موردنظر ارسال شد.\n{GroupChatReplyLink.Url(data.TargetChat,data.SentMessage!.Value)}",
        "failed"=>"⚠️ پاسخ ارسال نشد. "+data.Error,
        "unknown"=>"⚠️ نتیجهٔ ارسال مشخص نیست؛ برای جلوگیری از پاسخ تکراری، خودکار دوباره فرستاده نمی‌شود. ابتدا گروه را بررسی کنید.",
        _=>"⏳ پاسخ در صف ارسال است."};
    private async Task ShowGroupChatReplyStatusAsync(long chat,long actor,Guid id,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==GroupChatReplyChannel,ct)
            ??throw new InvalidOperationException("درخواست پاسخ پیدا نشد.");
        var data=JsonSerializer.Deserialize<GroupChatReplyJob>(row.Payload)!;
        if(data.Actor!=actor||data.AdminChat!=chat||row.Recipient!=chat.ToString())throw new InvalidOperationException("این درخواست متعلق به شما نیست.");
        await _sender.SendInlineKeyboardAsync(chat.ToString(),GroupChatReplyStatusText(data),
            [[new("🔄 مشاهدهٔ نتیجه",GroupChatReplyPrefix+"status:"+id.ToString("N"))],[new("💬 پاسخ تازه",GroupChatReplyPrefix+"start")]],ct);
    }

    private async Task SaveGroupChatReplyAsync(Guid id,GroupChatReplyJob data,CancellationToken ct)=>
        await _db.NotificationOutbox.Where(n=>n.Id==id&&n.Channel==GroupChatReplyChannel&&!n.IsDeleted).ExecuteUpdateAsync(s=>s
            .SetProperty(n=>n.Payload,JsonSerializer.Serialize(data,(JsonSerializerOptions?)null)).SetProperty(n=>n.UpdatedAt,DateTime.UtcNow),ct);

    internal async Task<int?> ProcessGroupChatReplyAsync(NotificationOutbox job,CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==job.Id&&n.Channel==GroupChatReplyChannel&&!n.IsDeleted,ct);
        var data=JsonSerializer.Deserialize<GroupChatReplyJob>(row.Payload)!;
        if(row.Recipient!=data.AdminChat.ToString()||data.AdminChat!=data.Actor)throw new InvalidOperationException("Reply job binding mismatch");
        if(data.State is not("sent" or "failed" or "unknown"))
        {
            if(data.AttemptedAt!=null)data=data with{State="unknown"};
            else if(data.TargetChat!=_options.SalesDiscussionChatId.Trim()||
                !GroupChatReplyLink.TryParse(GroupChatReplyLink.Url(data.TargetChat,data.TargetMessage),data.TargetChat,out _)||
                !await IsAuthorizedInvoiceActionAdminAsync(data.Actor,ct))
                data=data with{State="failed",Error="دسترسی ادمین یا مقصد گروه تغییر کرده است؛ دوباره بررسی کنید."};
            else
            {
                data=data with{State="sending",AttemptedAt=DateTime.UtcNow};await SaveGroupChatReplyAsync(job.Id,data,ct);
                var sent=await _sender.SendReplyOnceAsync(data.TargetChat,data.Text,data.TargetMessage,ct);
                if(sent.IsRetryable)
                {data=data with{State="queued",AttemptedAt=null};await SaveGroupChatReplyAsync(job.Id,data,ct);return Math.Max(1,sent.RetryAfterSeconds??60);}
                if(sent.IsSuccessful&&sent.MessageId is >0)data=data with{State="sent",SentMessage=sent.MessageId};
                else if(sent.Error?.Contains("message to be replied",StringComparison.OrdinalIgnoreCase)==true||sent.Error?.Contains("reply message not found",StringComparison.OrdinalIgnoreCase)==true||sent.Error?.Contains("reply_message_id_invalid",StringComparison.OrdinalIgnoreCase)==true)
                    data=data with{State="failed",Error="پیام مقصد پیدا نشد یا حذف شده؛ لینک درست را در یک درخواست تازه وارد کنید. پیام مستقلی در گروه ارسال نشد."};
                else data=data with{State="unknown"};
            }
            await SaveGroupChatReplyAsync(job.Id,data,ct);
        }
        if(!data.NoticeAttempted)
        {
            data=data with{NoticeAttempted=true};await SaveGroupChatReplyAsync(job.Id,data,ct);
            await _sender.SendReplyOnceAsync(data.AdminChat.ToString(),GroupChatReplyStatusText(data),data.SourceMessage,ct);
        }
        return null;
    }
}
