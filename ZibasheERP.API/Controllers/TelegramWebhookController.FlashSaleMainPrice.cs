using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;
public sealed partial class TelegramWebhookController
{
    internal async Task RefreshFlashSaleMainPriceAsync(Guid id,CancellationToken ct)
    {
        var gate=SalesListRefreshLocks.GetOrAdd(id,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct);
        try
        {
            var list=await _salesListRepository.GetByIdAsync(id,ct)??throw new InvalidOperationException("Sales list missing");
            await _db.Entry(list).ReloadAsync(ct);
            if(list.IsDeleted||list.IsInventoryOffer||list.TelegramMessageId is null||string.IsNullOrWhiteSpace(list.TelegramChannelId))return;
            if(await _db.SalesLists.AsNoTracking().AnyAsync(l=>!l.IsDeleted&&l.Id!=list.Id&&l.TelegramChannelId==list.TelegramChannelId&&
                l.TelegramMessageId==list.TelegramMessageId&&l.OpenDate>list.OpenDate&&(l.Status==SalesListStatus.Open||l.Status==SalesListStatus.Full),ct))return;
            var quote=await FlashSalePricing.QuoteAsync(_db,list,DateTime.UtcNow,ct);
            var confirmed=await _salesListRequestRepository.GetConfirmedAsync(id,ct);
            var requests=list.Status is SalesListStatus.Open or SalesListStatus.Full?confirmed:
                (await _salesListRequestRepository.GetForLabelAdministrationAsync(id,ct)).Concat(confirmed.Where(r=>r.Kind==SalesListRequestKind.NextBottle)).DistinctBy(r=>r.Id).ToArray();
            var pages=FormatPromotionChannelSalesListPages(list,requests,quote);
            await SynchronizeContinuationPostAsync(list,pages.Continuation,ct);
            var buttons=BuildChannelVolumeButtons(list,list.TelegramContinuationMessageId);
            var result=await _temporaryMessageCleaner.UpdatePostAsync(list.TelegramChannelId,list.TelegramMessageId.Value,
                ()=>!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)
                    ?_sender.EditPhotoCaptionAsync(list.TelegramChannelId,list.TelegramMessageId.Value,pages.Main,buttons,ct)
                    :_sender.EditTextWithKeyboardAsync(list.TelegramChannelId,list.TelegramMessageId.Value,pages.Main,buttons,ct),ct);
            if(!result.IsSuccessful&&!IsTelegramMessageUnchanged(result.Error))throw new FlashSalePriceRetryException(Math.Max(30,result.RetryAfterSeconds??30));
        }
        finally{gate.Release();}
    }
    internal sealed class FlashSalePriceRetryException(int seconds):Exception{public int Seconds{get;}=seconds;}
}
