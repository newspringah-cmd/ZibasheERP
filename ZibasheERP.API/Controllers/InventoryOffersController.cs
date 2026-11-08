using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZibasheERP.API.Inventory;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.API.Controllers;

[ApiController,Route("api/inventory-offers"),Authorize(Roles="Admin")]
public sealed class InventoryOffersController(AppDbContext db,IOptions<TelegramOptions> options,IInvoiceIssuanceService invoices):ControllerBase
{
    [HttpGet("{id:guid}/publication")]
    public async Task<IActionResult> Publication(Guid id,[FromQuery]Guid? batchId,CancellationToken ct)
    {
        var list=await db.SalesLists.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id&&!v.IsDeleted&&v.IsInventoryOffer&&v.InventoryCode!=null,ct);
        if(list is null)return NotFound();
        if(batchId.HasValue)
        {
            var reports=await invoices.GetPaymentTrackingReportsAsync(batchId.Value,ct);var report=reports.SingleOrDefault(v=>v.SalesListId==id);
            if(report is null)return NotFound();
            if(string.IsNullOrWhiteSpace(options.Value.NewPaymentsChatId))return Conflict();
            var link=await db.InvoiceIssuanceBatchSalesLists.AsNoTracking().SingleAsync(v=>v.InvoiceIssuanceBatchId==batchId&&v.SalesListId==id,ct);
            return Ok(new{Id=id,Code=list.DisplayCode,ChatId=options.Value.NewPaymentsChatId,Text=report.Message,
                Photo=list.TelegramPhotoFileId,MessageId=report.TelegramMessageId,PhotoMessageId=link.TelegramPaymentTrackingPhotoMessageId,
                Callback=(string?)null});
        }
        if(string.IsNullOrWhiteSpace(options.Value.ReadyInventoryChannelId))return Conflict();
        if(list.Status is not(SalesListStatus.Open or SalesListStatus.Full))return Conflict();
        if(list.Status!=SalesListStatus.Open&&!list.TelegramMessageId.HasValue)return Conflict();
        return Ok(new{Id=id,Code=list.DisplayCode,ChatId=options.Value.ReadyInventoryChannelId,Text=ReadyInventoryPresentation.Caption(list),
            Photo=list.TelegramPhotoFileId,MessageId=list.TelegramMessageId,PhotoMessageId=(long?)null,
            Callback=$"slv:{Convert.ToBase64String(list.Id.ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_')}:{list.TotalVolume}"});
    }
    public static string PostUrl(string? chat,long? message)
    {
        if(message is null||string.IsNullOrWhiteSpace(chat))return "";
        if(chat.StartsWith("-100",StringComparison.Ordinal)&&chat[4..].All(char.IsDigit))return $"https://t.me/c/{chat[4..]}/{message}";
        if(chat.StartsWith('@')&&chat[1..].All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))return $"https://t.me/{chat[1..]}/{message}";
        return "";
    }
    public sealed record Receipt(long MessageId,long? PhotoMessageId=null,Guid? BatchId=null);
    [HttpPost("{id:guid}/receipt")]
    public async Task<IActionResult> Bind(Guid id,Receipt receipt,CancellationToken ct)
    {
        if(receipt.MessageId<=0)return BadRequest();
        if(receipt.BatchId.HasValue)
        {
            var link=await db.InvoiceIssuanceBatchSalesLists.SingleOrDefaultAsync(v=>v.InvoiceIssuanceBatchId==receipt.BatchId&&v.SalesListId==id,ct);
            if(link is null)return NotFound();
            if(link.TelegramPaymentTrackingMessageId.HasValue&&link.TelegramPaymentTrackingMessageId!=receipt.MessageId)return Conflict();
            link.TelegramPaymentTrackingPhotoMessageId=receipt.PhotoMessageId;
            await invoices.SetPaymentTrackingMessageAsync(receipt.BatchId.Value,id,options.Value.NewPaymentsChatId,receipt.MessageId,ct);
            return NoContent();
        }
        var changed=await db.SalesLists.Where(v=>v.Id==id&&!v.IsDeleted&&v.InventoryCode!=null&&(v.TelegramMessageId==null||v.TelegramMessageId==receipt.MessageId))
            .ExecuteUpdateAsync(v=>v.SetProperty(x=>x.TelegramMessageId,receipt.MessageId).SetProperty(x=>x.TelegramChannelId,options.Value.ReadyInventoryChannelId),ct);
        return changed==1?NoContent():Conflict();
    }
}
