using System.Collections.Concurrent;
using System.Text.Json;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    private sealed record DebtApproval(long Actor,TelegramCallbackQuery? Callback,TelegramMessage? Command,
        string UserId,string? Username,decimal Amount,DateTime Until,string? Operation=null,long PromptId=0);
    private sealed record DebtGrant(decimal Amount,DateTime Until);
    private static readonly ConcurrentDictionary<Guid,DebtApproval> DebtApprovals=new();
    private static readonly ConcurrentDictionary<string,DebtGrant> DebtGrants=new();
    private static readonly ConcurrentDictionary<string,string> DebtResuming=new();
    private static string DebtGrantKey(long actor,TelegramCallbackQuery? callback,TelegramMessage? command,string userId,string? username,string? operation=null)=>
        $"{actor}:{callback?.Message?.Chat.Id??command?.Chat.Id}:{callback?.Message?.MessageId??command?.MessageId}:{callback?.Data??command?.Text}:{userId}:{username?.Trim().TrimStart('@').ToLowerInvariant()}:{operation}";

    private async Task<bool> AllowRegistrationDebtAsync(TelegramCallbackQuery? callback,TelegramMessage? command,
        string userId,string? username,CancellationToken ct,string? operation=null)
    {
        var actor=callback?.From.Id??command!.From!.Id;
        var debt=await new RegistrationDebtService(_db).GetAsync(userId,username,ct);
        var key=DebtGrantKey(actor,callback,command,userId,username,operation);
        var origin=DebtGrantKey(actor,callback,command,"",null);
        if(DebtResuming.TryGetValue(origin,out var expected)&&expected!=key)
            throw new InvalidOperationException("اطلاعات مشتری یا آیتم تغییر کرده است؛ ثبت را دوباره بررسی و تأیید کنید.");
        if(!debt.BlocksRegistration){DebtGrants.TryRemove(key,out _);return true;}
        if(!await IsAuthorizedInvoiceActionAdminAsync(actor,ct))
        {
            if(callback is not null)await _sender.AnswerCallbackAsync(callback.Id,RegistrationDebtService.CustomerMessage,ct,true);
            return false;
        }
        if(DebtGrants.TryRemove(key,out var grant)&&grant.Until>DateTime.UtcNow&&debt.OutstandingToman<=grant.Amount)return true;
        foreach(var old in DebtApprovals.Where(p=>p.Value.Until<DateTime.UtcNow).ToArray())DebtApprovals.TryRemove(old.Key,out _);
        foreach(var old in DebtGrants.Where(p=>p.Value.Until<DateTime.UtcNow).ToArray())DebtGrants.TryRemove(old.Key,out _);
        var token=Guid.NewGuid();var approval=new DebtApproval(actor,callback,command,userId,username,debt.OutstandingToman,DateTime.UtcNow.AddMinutes(10),operation);
        DebtApprovals[token]=approval;
        var sent=await _sender.SendInlineKeyboardAsync(actor.ToString(),
            $"⚠️ محدودیت بدهی مشتری\nمشتری: {(string.IsNullOrWhiteSpace(username)?userId:"@"+username.TrimStart('@'))}\nماندهٔ فاکتورهای در انتظار پرداخت: {debt.OutstandingToman:#,0.##} تومان\nسقف مجاز: ۱۰٬۰۰۰٬۰۰۰ تومان\n\nثبت توسط مشتری مسدود است. آیا با اختیار خودتان همین آیتم را ثبت می‌کنید؟",
            [[new("✅ ثبت با وجود بدهی",$"debtlimit:yes:{token:N}")],[new("❌ انصراف",$"debtlimit:no:{token:N}")]],ct);
        if(sent.IsSuccessful&&sent.MessageId is >0)DebtApprovals[token]=approval with{PromptId=sent.MessageId.Value};
        else DebtApprovals.TryRemove(token,out _);
        if(callback is not null)await _sender.AnswerCallbackAsync(callback.Id,
            sent.IsSuccessful?"مشتری بدهکار است؛ تأیید ثبت با وجود بدهی را در خصوصی زیبا بررسی کنید.":"ارسال تأیید بدهی ناموفق بود؛ ابتدا خصوصی ربات را باز کنید و دوباره تلاش کنید.",ct,true);
        if(command is not null&&(command.Chat.Id!=actor||!sent.IsSuccessful))
            await ReplyAsync(command.Chat.Id,sent.IsSuccessful?
                "این ثبت نیاز به تأیید ادمین در خصوصی زیبا دارد؛ پیام خصوصی را بررسی کنید.":
                "ارسال تأیید خصوصی ناموفق بود؛ خصوصی ربات را باز کنید و دستور را دوباره بفرستید.",ct);
        return false;
    }
    private Task<bool> AllowCallbackRegistrationDebtAsync(TelegramCallbackQuery callback,string userId,string? username,CancellationToken ct,string? operation=null)=>
        AllowRegistrationDebtAsync(callback,null,userId,username,ct,operation);
    private Task<bool> AllowCommandRegistrationDebtAsync(TelegramMessage message,string userId,string? username,CancellationToken ct)=>
        AllowRegistrationDebtAsync(null,message,userId,username,ct);

    private static string ChannelDebtOperation(SalesListRequest r)=>JsonSerializer.Serialize(new{r.Id,
        Volume=r.VolumeMl.ToString("0.####",System.Globalization.CultureInfo.InvariantCulture),r.BottleId,r.IsBottleOwner,r.IsGift,
        r.GiftRecipientTelegramUsername,r.GiftRecipientTelegramUserId,Price=r.PerfumePricePerMl.ToString("0.####",System.Globalization.CultureInfo.InvariantCulture)});

    private async Task<string?> CurrentDebtOperationAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var chat=callback.Message!.Chat.Id;var actor=callback.From.Id;var data=callback.Data??"";
        if(data.StartsWith("adminrequest:",StringComparison.Ordinal))
        {
            if(!_adminRequestDrafts.TryGet(chat,actor,out var d)||d.Stage!=TelegramAdminRequestStage.AwaitingConfirmation)return null;
            return JsonSerializer.Serialize(new{d.SalesListId,d.VolumeMl,d.Kind,d.IsGift,d.GiftRecipientIdentity,d.IsBottleOwner,d.IsComplimentaryBottle,d.BottleType,d.RequestNotes});
        }
        if(data.StartsWith("invoiceadmin:ready:",StringComparison.Ordinal))
            return InventoryDrafts.TryGetValue((chat,actor),out var d)&&d.Step=="buy-confirm"?$"{d.Id:N}:{d.Token}":null;
        if(data.StartsWith("adminlist:",StringComparison.Ordinal))
        {
            if(!_adminSalesListDrafts.TryGet(chat,actor,out var d)||d.Stage!=TelegramAdminSalesListStage.Preview||d.SalesListId.HasValue)return null;
            return JsonSerializer.Serialize(new{d.PerfumeId,d.PersianName,d.EnglishName,d.PhotoFileId,d.TotalVolume,d.PricePerMl,d.BottleOwnerVolumeMl});
        }
        var parts=data.Split(':');
        if(parts.Length>=2&&(parts[0] is "sly" or "slb" or "slp")&&TryDecodeCompactGuid(parts[1],out var id))
        {
            var r=await _salesListRequestRepository.GetAsync(id,ct);
            if(r is null||r.Status!=SalesListRequestStatus.PendingConfirmation)return null;
            return ChannelDebtOperation(r);
        }
        return null;
    }

    private async Task<bool> TryHandleRegistrationDebtApprovalAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Data?.StartsWith("debtlimit:",StringComparison.Ordinal)!=true)return false;
        var parts=callback.Data.Split(':');
        if(parts.Length!=3||!Guid.TryParseExact(parts[2],"N",out var token)||
            !DebtApprovals.TryGetValue(token,out var approval)||approval.Until<DateTime.UtcNow||
            approval.Actor!=callback.From.Id||callback.Message?.Chat.Type!="private"||
            callback.Message.Chat.Id!=callback.From.Id||callback.Message.MessageId!=approval.PromptId||
            !await IsAuthorizedInvoiceActionAdminAsync(callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"این تأیید منقضی است یا متعلق به شما نیست؛ ثبت را دوباره شروع کنید.",ct,true);return true;}
        if(parts[1] is not("yes" or "no"))return true;
        if(parts[1]=="yes"&&approval.Callback is not null&&approval.Operation is not null&&
            await CurrentDebtOperationAsync(approval.Callback,ct)!=approval.Operation)
        {
            DebtApprovals.TryRemove(token,out _);
            await _sender.AnswerCallbackAsync(callback.Id,"مشخصات آیتم یا پیش‌نویس تغییر کرده است؛ ثبت را دوباره بررسی و تأیید کنید.",ct,true);
            await _sender.EditTextWithKeyboardAsync(callback.From.Id.ToString(),callback.Message.MessageId,
                "❌ این تأیید به‌علت تغییر مشخصات آیتم منقضی شد؛ آیتمی ثبت نشد.",[],ct);
            return true;
        }
        if(!DebtApprovals.TryRemove(token,out _)){await _sender.AnswerCallbackAsync(callback.Id,"این تصمیم قبلاً ثبت شده است.",ct,true);return true;}
        if(parts[1]=="no")
        {
            await _sender.AnswerCallbackAsync(callback.Id,"ثبت با وجود بدهی لغو شد؛ آیتمی اضافه نشد.",ct,true);
            await _sender.EditTextWithKeyboardAsync(callback.From.Id.ToString(),callback.Message.MessageId,"❌ ثبت با وجود بدهی لغو شد.",[],ct);return true;
        }
        var fresh=await new RegistrationDebtService(_db).GetAsync(approval.UserId,approval.Username,ct);
        // The ordinary flow re-reads debt and asks again if it has increased.
        var key=DebtGrantKey(approval.Actor,approval.Callback,approval.Command,approval.UserId,approval.Username,approval.Operation);
        var origin=DebtGrantKey(approval.Actor,approval.Callback,approval.Command,"",null);
        _db.NotificationOutbox.Add(new(){Id=token,CustomerId=fresh.CustomerId??Guid.Empty,Channel="AdminDebtOverride",EventType="AdminDebtOverrideApproved",Recipient=approval.Actor.ToString(),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow,
            Payload=JsonSerializer.Serialize(new{Actor=approval.Actor,approval.UserId,approval.Username,ShownDebtToman=approval.Amount,CurrentDebtToman=fresh.OutstandingToman,LimitToman=RegistrationDebtService.LimitToman,Origin=approval.Callback?.Data??approval.Command?.Text,approval.Operation})});
        await _db.SaveChangesAsync(ct);
        await _sender.EditTextWithKeyboardAsync(callback.From.Id.ToString(),callback.Message.MessageId,"✅ اختیار ثبت با وجود بدهی تأیید شد؛ نتیجهٔ ثبت در همان جریان اصلی اعلام می‌شود.",[],ct);
        DebtGrants[key]=new(Math.Max(approval.Amount,RegistrationDebtService.LimitToman),DateTime.UtcNow.AddMinutes(2));
        DebtResuming[origin]=key;
        try
        {
            if(approval.Callback is not null)await HandleCallbackAsync(approval.Callback with{Id=callback.Id,From=callback.From},ct);
            else
            {
                await _sender.AnswerCallbackAsync(callback.Id,"در حال بررسی و ثبت با اختیار ادمین…",ct);
                await TryHandleAdminCommandAsync(approval.Command!,ct);
            }
        }
        finally{DebtGrants.TryRemove(key,out _);DebtResuming.TryRemove(origin,out _);}
        return true;
    }
}
