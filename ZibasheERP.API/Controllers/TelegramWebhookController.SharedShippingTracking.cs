using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private sealed record SharedTrackingTarget(Guid CustomerId,string ChatId);
    private async Task<string[]?> SharedTrackingDestinationsAsync(TrackingDispatch dispatch,CancellationToken ct)
    {
        if(dispatch.ShippingRequestId is not Guid request)return null;
        var parcel=await SharedShippingService.ReadAsync(_db,request,ct);if(parcel is null)return null;
        if(dispatch.CustomerId!=parcel.PrimaryCustomerId)throw new ShippingRecipientValidationException(request,[],"گیرندهٔ اصلی رهگیری با پست مشترک تطبیق ندارد.");
        var targets=new List<SharedTrackingTarget>();
        foreach(var customer in parcel.CustomerIds.Order())
        {
            var groups=await _db.CustomerTelegramGroups.AsNoTracking().Where(g=>!g.IsDeleted&&g.IsActive&&g.CustomerId==customer).Take(2).ToArrayAsync(ct);
            if(groups.Length!=1||!long.TryParse(groups[0].ChatId,out var chat)||chat>=0||
                await _db.CustomerTelegramGroups.AnyAsync(g=>!g.IsDeleted&&g.IsActive&&g.ChatId==groups[0].ChatId&&g.CustomerId!=customer,ct))
            {
                var member=await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==customer&&!c.IsDeleted,ct)
                    ??throw new ShippingRecipientValidationException(request,[],"یکی از اعضای پست مشترک دیگر فعال نیست.");
                // Annotation-only legacy companions have no ERP items to dispatch and cannot supply a guessed destination.
                if(customer!=parcel.PrimaryCustomerId&&!await ShippingPackageService.CustomerItems(_db,member).AsNoTracking().AnyAsync(i=>i.ShippingRequestId==request,ct))continue;
                throw new ShippingRecipientValidationException(request,[],"یکی از اعضای دارای آیتم پست مشترک گروه حسابداری یکتا و متصل ندارد؛ ابتدا اتصال گروه‌ها اصلاح شود.");
            }
            targets.Add(new(customer,groups[0].ChatId));
        }
        var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("shared-tracking-targets:"+dispatch.Id))[..16]);
        var existing=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&!n.IsDeleted,ct);
        var json=JsonSerializer.Serialize(targets);
        if(existing!=null)
        {
            if(existing.Channel!="SharedTrackingTargets"||existing.CustomerId!=dispatch.CustomerId||existing.Payload!=json)
                throw new ShippingRecipientValidationException(request,[],"اعضا یا گروه مقصد پس از آغاز رهگیری تغییر کرده؛ ادامهٔ ارسال نیازمند بررسی است.");
        }
        else
        {
            _db.NotificationOutbox.Add(new(){Id=id,CustomerId=dispatch.CustomerId!.Value,Channel="SharedTrackingTargets",EventType="TrackingDestinationSnapshot",
                Recipient=dispatch.Id.ToString("N"),Payload=json,Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow,ProcessedAt=DateTime.UtcNow});
            await _db.SaveChangesAsync(ct);
        }
        return targets.Select(t=>t.ChatId).Distinct().ToArray();
    }

    private async Task QueueSharedTrackingPhotoAsync(TelegramMessage message,TelegramShippingTrackingPhotoDraft draft,string fileId,CancellationToken ct)
    {
        var parcel=await SharedShippingService.ReadAsync(_db,draft.ShippingRequestId,ct)??throw new InvalidOperationException("پست مشترک پیدا نشد.");
        await ShippingPackageService.ValidateDispatchAsync(_db,parcel.RequestId,draft.CustomerId,ct);
        var file=await _sender.DownloadFileAsync(fileId,ct);
        if(!file.IsSuccessful||file.Content is not {Length:>0})throw new InvalidOperationException("دریافت عکس رهگیری کامل نشد؛ دوباره عکس را بفرستید.");
        var sourceHash=Convert.ToHexString(SHA256.HashData(file.Content.Concat(parcel.RequestId.ToByteArray()).ToArray()));
        await using var tx=await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r=sp_getapplock @Resource={"shared-shipping:"+parcel.RequestId},@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r<0 THROW 51000,'Shared parcel busy',1;",ct);
        parcel=await SharedShippingService.ReadAsync(_db,parcel.RequestId,ct)??throw new InvalidOperationException("پست مشترک پیدا نشد.");
        if(parcel.ClosedAt!=null)throw new InvalidOperationException("این بسته قبلاً ارسال شده است.");
        var active=await _db.TrackingDispatches.AsNoTracking().Where(d=>!d.IsDeleted&&d.ShippingRequestId==parcel.RequestId&&
            (d.Status==TrackingDispatchStatus.Sending||d.Status==TrackingDispatchStatus.Sent||d.ImportBatch!.ConfirmedAt!=null)).Select(d=>new{d.Id,d.ImportBatchId}).FirstOrDefaultAsync(ct);
        Guid batchId;
        if(active!=null)batchId=active.ImportBatchId;
        else
        {
            var batch=await _db.TrackingImportBatches.SingleOrDefaultAsync(b=>!b.IsDeleted&&b.SourceHash==sourceHash,ct);
            if(batch is null)
            {
                var now=DateTime.UtcNow;batchId=Guid.NewGuid();
                var address=await _db.Addresses.AsNoTracking().SingleAsync(a=>a.Id==parcel.AddressId&&!a.IsDeleted,ct);
                _db.TrackingImportBatches.Add(new(){Id=batchId,SourceHash=sourceHash,Carrier=TrackingCarrier.IranPost,
                    RequestedByTelegramUserId=message.From!.Id,SourceChatId=message.Chat.Id,SourceMessageId=message.MessageId,CreatedAt=now});
                _db.TrackingDispatches.Add(new(){Id=Guid.NewGuid(),ImportBatchId=batchId,Carrier=TrackingCarrier.IranPost,
                    TrackingCode="shared-photo-"+parcel.RequestId.ToString("N"),RecipientName=address.ReceiverName,Destination=address.City,
                    CustomerId=parcel.PrimaryCustomerId,ShippingRequestId=parcel.RequestId,Status=TrackingDispatchStatus.Ready,
                    MatchNotes="SharedManualTrackingPhoto",CardImage=file.Content,CreatedAt=now});
                await _db.SaveChangesAsync(ct);
            }
            else batchId=batch.Id;
        }
        // Creating the dispatch and its durable job is atomic, even if the process
        // stops before the operator sees the queued acknowledgement.
        var dispatch=await _db.TrackingDispatches.SingleAsync(d=>d.ImportBatchId==batchId&&d.ShippingRequestId==parcel.RequestId&&!d.IsDeleted,ct);
        var outbox=await _db.NotificationOutbox.SingleOrDefaultAsync(n=>n.Id==dispatch.Id,ct);
        if(outbox is null)_db.NotificationOutbox.Add(new(){Id=dispatch.Id,CustomerId=parcel.PrimaryCustomerId,Channel="TrackingDispatch",EventType="TrackingDispatch",
            Recipient=message.Chat.Id.ToString(),Payload=JsonSerializer.Serialize(new ZibasheERP.API.Tracking.TrackingDispatchJob(batchId,dispatch.Id,message.Chat.Id)),CreatedAt=DateTime.UtcNow});
        var storedBatch=await _db.TrackingImportBatches.SingleAsync(b=>b.Id==batchId,ct);storedBatch.ConfirmedAt??=DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        _orderFlowDrafts.ClearShippingTrackingPhoto(message.Chat.Id,message.From!.Id);
        var note=parcel.LegacyUsernames is {Length:>0}?"\nهمراهان صرفاً یادداشتی: "+string.Join("، ",parcel.LegacyUsernames.Select(n=>"@"+n))+"؛ برای ارسال خودکار کد، ابتدا گروهشان باید متصل شود.":string.Empty;
        await _sender.SendKeyboardDismissAsync(message.Chat.Id.ToString(),"✅ عکس رهگیری در صف ارسال به گروه‌های متصل پست مشترک ثبت شد. مقصدهای موفق دوباره ارسال نمی‌شوند؛ نتیجه در گزارش ارسال سری آخر قابل مشاهده است."+note,ct);
    }
}
