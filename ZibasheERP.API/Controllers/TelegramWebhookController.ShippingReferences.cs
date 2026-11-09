using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Services;

namespace ZibasheERP.API.Controllers;

public sealed partial class TelegramWebhookController
{
    private const string ShippingReferenceChannel = "ShippingReferenceContext";
    private const string ShippingReferencePrefix = "shipping:refs:";
    private sealed record ShippingReferenceContext(Guid CustomerId, Guid? RequestId, Guid[] ItemIds,
        string ChatId, string Kind = "root", Guid? SalesListId = null, int Offset = 0,
        long? MessageId = null, bool Installed = false, Guid? AddressId = null, Guid? ViewId = null,bool LegacyAddressOnly=false);

    private async Task<Guid> SaveShippingReferenceAsync(ShippingReferenceContext context, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _db.NotificationOutbox.Add(new NotificationOutbox { Id=id, CustomerId=context.CustomerId, Channel=ShippingReferenceChannel,
            EventType=context.Kind=="root"?"AddressReferences":"ReferenceSelection", Recipient=context.ChatId,
            Status=NotificationOutboxStatus.Processed, CreatedAt=DateTime.UtcNow, ProcessedAt=DateTime.UtcNow, Payload=JsonSerializer.Serialize(context) });
        await _db.SaveChangesAsync(ct); return id;
    }
    private async Task BindShippingReferenceAsync(Guid id, long? messageId, CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==id,ct);
        var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
        await _db.NotificationOutbox.Where(n=>n.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,
            JsonSerializer.Serialize(context with{MessageId=messageId,Installed=true},(JsonSerializerOptions?)null)).SetProperty(n=>n.UpdatedAt,DateTime.UtcNow),ct);
    }
    private static IReadOnlyCollection<TelegramInlineButton>[] ShippingReferenceButtons(Guid id) =>
    [
        [new("📸 نمایش عکس دکانت‌ها",ShippingReferencePrefix+"photos:"+id.ToString("N"))],
        [new("📋 نمایش لیست تکمیل‌شده",ShippingReferencePrefix+"lists:"+id.ToString("N"))],
        [new("📷 ارسال عکس جهت تأیید مشتری",ShippingReferencePrefix+"start:"+id.ToString("N"))],
        [new("🖼 آخرین عکس ارسالی قبلی",ShippingReferencePrefix+"last:"+id.ToString("N"))]
    ];
    private async Task<(ShippingReferenceContext Context,OrderItem[] Items)> LoadShippingReferenceAsync(Guid id, TelegramCallbackQuery callback, CancellationToken ct)
    {
        var row=await _db.NotificationOutbox.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&!n.IsDeleted&&n.Channel==ShippingReferenceChannel,ct)
            ??throw new InvalidOperationException("مرجع این دکمه پیدا نشد؛ گزارش آدرس را دوباره باز کنید.");
        var context=JsonSerializer.Deserialize<ShippingReferenceContext>(row.Payload)!;
        if(row.CustomerId!=context.CustomerId||row.Recipient!=callback.Message!.Chat.Id.ToString()||context.ChatId!=row.Recipient||
           context.MessageId.HasValue&&context.MessageId!=callback.Message.MessageId)
            throw new InvalidOperationException("این دکمه متعلق به همین پیام و گروه نیست.");
        var items=await LoadShippingReferenceItemsAsync(context,ct);
        return(context,items);
    }
    private async Task<OrderItem[]> LoadShippingReferenceItemsAsync(ShippingReferenceContext context,CancellationToken ct)
    {
        var customer=await _db.Customers.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==context.CustomerId&&!c.IsDeleted,ct)
            ??throw new InvalidOperationException("حساب گیرنده قابل تأیید نیست.");
        if(context.RequestId.HasValue)
        {
            await GiftShippingMembershipService.RepairAsync(_db,context.RequestId.Value,ct);
            await ShippingPackageService.ValidatePackageAsync(_db,context.RequestId.Value,context.CustomerId,ct);
        }
        var source=context.RequestId.HasValue?SharedShippingService.Items(_db,context.RequestId.Value):CustomerShippingItems(customer);
        return await source.AsNoTracking().Where(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null&&
            (context.RequestId.HasValue?i.ShippingRequestId==context.RequestId:context.ItemIds.Contains(i.Id))).OrderBy(i=>i.CreatedAt).ThenBy(i=>i.Id).ToArrayAsync(ct);
    }
    private async Task<bool> TryHandleShippingReferenceCallbackAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        if(callback.Message is null||callback.Data?.StartsWith(ShippingReferencePrefix,StringComparison.Ordinal)!=true)return false;
        var parts=callback.Data[ShippingReferencePrefix.Length..].Split(':');
        if(parts.Length!=2||!Guid.TryParseExact(parts[1],"N",out var id))return true;
        var action=parts[0];
        if(action is "approve" or "revise" or "fixsend" or "fixcancel" or "fixedit")
        {
            try
            {
                if(action is "approve" or "revise")await HandleShippingPackCustomerApprovalAsync(callback,id,action=="approve",ct);
                else await HandleShippingPackRevisionCallbackAsync(callback,id,action,ct);
            }
            catch(InvalidOperationException e){await ApprovalResponseAsync(callback,e.Message,ct);}
            return true;
        }
        if(!await IsAuthorizedShippingAdminAsync(callback.Message.Chat.Id,callback.From.Id,ct))
        {await _sender.AnswerCallbackAsync(callback.Id,"دسترسی مسئول آماده‌سازی ندارید.",ct,true);return true;}
        try
        {
            if(action=="close"){await CloseShippingReferenceViewAsync(callback,id,ct);return true;}
            if(action=="packclear"){await HandleShippingPackCleanupAsync(callback,id,ct);return true;}
            if(action is "add" or "send" or "cancel") {await HandleShippingPackDraftCallbackAsync(callback,id,action,ct);return true;}
            var (context,items)=await LoadShippingReferenceAsync(id,callback,ct);
            if(context.Kind!="root"&&context.Kind!=action)throw new InvalidOperationException("نوع دکمه با این انتخاب یکسان نیست.");
            await _sender.AnswerCallbackAsync(callback.Id,cancellationToken:ct);
            if(action is "photos" or "lists" or "list" or "last")
            {
                _shippingReferenceViewId=await OpenShippingReferenceViewAsync(context,ct);
                context=context with{ViewId=_shippingReferenceViewId};
            }
            if(action=="last"){await SendPreviousShippingPackPhotosAsync(callback.Message.Chat.Id,context,ct);await FinishShippingReferenceViewAsync(callback.Message.Chat.Id,ct);return true;}
            if(action is "start" or "restart"&&items.Length==0&&context.RequestId is null&&context.ItemIds.Length==0)
            {
                context=context with{LegacyAddressOnly=true};
                await AssertLegacyPackAddressAsync(context,ct);
                await StartShippingPackPhotoDraftAsync(callback,context,items,ct,action=="restart");
                return true;
            }
            if(items.Length==0){await ShippingReferenceTextAsync(callback.Message.Chat.Id,"آیتم آمادهٔ ارسالی برای این پیام باقی نمانده است.",ct);await FinishShippingReferenceViewAsync(callback.Message.Chat.Id,ct);return true;}
            if(action=="photos")await SendShippingDecantReferencesAsync(callback.Message.Chat.Id,context,items,callback.From.Id,ct);
            else if(action=="lists")await SendShippingCompletedListMenuAsync(callback.Message.Chat.Id,context,items,ct);
            else if(action=="list")await SendShippingSelectedCompletedListAsync(callback.Message.Chat.Id,context,items,ct);
            else if(action is "start" or "restart")
            {
                context=await EnsureShippingReferenceParcelAsync(id,context,items,ct);
                await StartShippingPackPhotoDraftAsync(callback,context,items,ct,action=="restart");
            }
            await FinishShippingReferenceViewAsync(callback.Message.Chat.Id,ct);
        }
        catch(InvalidOperationException e)
        {
            if(_shippingReferenceViewId.HasValue){await ShippingReferenceTextAsync(callback.Message.Chat.Id,"⚠️ "+e.Message,ct);await FinishShippingReferenceViewAsync(callback.Message.Chat.Id,ct);}
            else await _sender.AnswerCallbackAsync(callback.Id,e.Message,ct,true);
        }
        finally{_shippingReferenceViewId=null;}
        return true;
    }

    private static IReadOnlyCollection<TelegramInlineButton>[] ShippingParcelButtons(Guid requestId) =>
    [
        [new("☑️ انتخاب برای گزارش کلی",$"shipping:select:{requestId:N}"),new("📊 گزارش کلی انتخاب‌ها","shipping:batchreport")],
        [new("✅ ارسال شد",$"shipping:sent:{requestId:N}"),new("📸 ارسال کد رهگیری",$"shipping:trackingbatch:{requestId:N}")]
    ];
    private async Task<ShippingReferenceContext> EnsureShippingReferenceParcelAsync(Guid contextId,ShippingReferenceContext context,OrderItem[] items,CancellationToken ct)
    {
        if(context.RequestId.HasValue)return context;
        if(!context.AddressId.HasValue||!context.MessageId.HasValue)throw new InvalidOperationException("برای عکس تأیید، درخواست ارسال مشخص لازم است؛ ابتدا بسته را از منوی آماده‌سازی ثبت کنید.");
        var assigned=items.Where(i=>i.ShippingRequestId.HasValue).Select(i=>i.ShippingRequestId!.Value).Distinct().ToArray();
        Guid requestId;
        if(assigned.Length==1&&items.All(i=>i.ShippingRequestId==assigned[0]))
        {
            requestId=assigned[0];var owner=await ShippingPackageService.ValidatePackageAsync(_db,requestId,context.CustomerId,ct);
            var whole=await SharedShippingService.Items(_db,requestId).AsNoTracking().Where(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null).Select(i=>i.Id).ToArrayAsync(ct);
            if(!whole.ToHashSet().SetEquals(items.Select(i=>i.Id)))throw new InvalidOperationException("اقلام آدرس با کل بسته یکسان نیست؛ از پیام درخواست اصلی بسته استفاده کنید.");
        }
        else if(assigned.Length!=0)throw new InvalidOperationException("این آدرس شامل چند بسته یا اقلام با درخواست متفاوت است؛ ابتدا درخواست ارسال مشخص شود.");
        else
        {
            await using var tx=await _db.Database.BeginTransactionAsync(ct);
            await _db.Customers.FromSqlInterpolated($"SELECT * FROM Customers WITH(UPDLOCK,HOLDLOCK) WHERE Id={context.CustomerId}").AsNoTracking().SingleAsync(ct);
            if(!await _db.Addresses.AsNoTracking().AnyAsync(a=>a.Id==context.AddressId&&a.CustomerId==context.CustomerId&&!a.IsDeleted,ct))throw new InvalidOperationException("آدرس این بسته با گیرنده تطبیق ندارد.");
            var fresh=await LoadShippingReferenceItemsAsync(context,ct);
            if(!fresh.Select(i=>i.Id).ToHashSet().SetEquals(items.Select(i=>i.Id))||fresh.Any(i=>i.ShippingRequestId.HasValue))throw new InvalidOperationException("درخواست اقلام تغییر کرده؛ از آدرس جدید استفاده کنید.");
            requestId=Guid.NewGuid();var ids=fresh.Select(i=>i.Id).ToArray();var now=DateTime.UtcNow;
            var count=await _db.OrderItems.Where(i=>ids.Contains(i.Id)&&i.ShippingRequestId==null&&!i.IsDeleted&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null)
                .ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippingRequestId,(Guid?)requestId).SetProperty(i=>i.ShippingRequestedAt,(DateTime?)now)
                    .SetProperty(i=>i.ShippingTelegramMessageId,context.MessageId).SetProperty(i=>i.UpdatedAt,(DateTime?)now),ct);
            if(count!=ids.Length)throw new InvalidOperationException("عضویت اقلام تغییر کرده؛ بسته ثبت نشد.");
            var orderIds=fresh.Select(i=>i.OrderId).Distinct().ToArray();
            await _db.Orders.Where(o=>orderIds.Contains(o.Id)).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.DeliveryAddressId,context.AddressId),ct);
            await ShippingPackageService.ValidatePackageAsync(_db,requestId,context.CustomerId,ct);await tx.CommitAsync(ct);
        }
        context=context with{RequestId=requestId};
        await _db.NotificationOutbox.Where(n=>n.Id==contextId).ExecuteUpdateAsync(s=>s.SetProperty(n=>n.Payload,JsonSerializer.Serialize(context,(JsonSerializerOptions?)null)),ct);
        await _sender.EditReplyMarkupAsync(context.ChatId,context.MessageId.Value,ShippingParcelButtons(requestId).Concat(ShippingReferenceButtons(contextId)).ToArray(),ct);
        return context;
    }

    private async Task SendShippingDecantReferencesAsync(long chat,ShippingReferenceContext context,OrderItem[] items,long actor,CancellationToken ct)
    {
        var listIds=items.Where(i=>i.SalesListId.HasValue).Select(i=>i.SalesListId!.Value).Distinct().ToArray();
        var listKeys=listIds.Select(id=>id.ToString()).ToArray();
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel=="Telegram"&&n.EventType=="DecantPhotoDelivery"&&listKeys.Any(key=>n.Payload.Contains(key)))
            .OrderByDescending(n=>n.CreatedAt).Select(n=>new{n.CustomerId,n.Payload,n.CreatedAt}).ToArrayAsync(ct);
        var photos=new Dictionary<Guid,(string File,DateTime At)>();
        foreach(var row in rows)
        {
            try
            {
                using var payload=JsonDocument.Parse(row.Payload);var data=payload.RootElement;
                if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("SalesListId",out var code)||code.ValueKind!=JsonValueKind.String||!code.TryGetGuid(out var listId)||!listIds.Contains(listId)||photos.ContainsKey(listId))continue;
                if(data.TryGetProperty("FileId",out var file)&&file.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(file.GetString()))photos[listId]=(file.GetString()!,row.CreatedAt);
            }
            catch(JsonException){ /* A malformed historical receipt must not hide other valid photos. */ }
        }
        var result=new List<(string File,string Name,decimal Volume)>();var missing=new List<string>();
        var shared=context.RequestId.HasValue?await SharedShippingService.ReadAsync(_db,context.RequestId.Value,ct):null;
        var labels=shared is null?null:await ShippingRecipientLabelsAsync(items,ct);
        foreach(var group in items.GroupBy(i=>(i.SalesListId.HasValue?"list:"+i.SalesListId:"item:"+i.Id)+(labels is null?"":" — "+labels[i.Id])))
        {
            var first=group.First();var name=ShippingItemName(first)+(first.SalesList is null?"":" — "+first.SalesList.DisplayCode);
            if(labels!=null)name+=" — "+labels[first.Id];
            var volume=group.Sum(i=>i.RequestedVolumeMl);
            if(first.SalesListId.HasValue&&photos.TryGetValue(first.SalesListId.Value,out var photo)){result.Add((photo.File,name,volume));continue;}
            // An order-wide photo is safe only when every live item of that order
            // belongs to this exact ready-item set, not another gift recipient.
            var orderId=first.OrderId;var ownedIds=items.Select(i=>i.Id).ToArray();
            var completeOrder=!await _db.OrderItems.AsNoTracking().AnyAsync(i=>!i.IsDeleted&&i.OrderId==orderId&&!ownedIds.Contains(i.Id),ct);
            var artifact=completeOrder?await _db.OrderArtifacts.AsNoTracking().Where(a=>!a.IsDeleted&&a.OrderId==orderId&&a.Type==OrderArtifactType.DecantPhoto&&a.ExternalFileId!=null)
                .OrderByDescending(a=>a.DeliveredAt).Select(a=>a.ExternalFileId).FirstOrDefaultAsync(ct):null;
            if(!string.IsNullOrWhiteSpace(artifact))result.Add((artifact!,name,volume));else missing.Add(name);
        }
        var stream=BuildShippingDecantPhotos(result).Skip(context.Offset).ToArray();
        await QueueShippingDecantStreamAsync(context,items,actor,stream,missing.ToArray(),ct);
    }

    private async Task<SalesList[]> ShippingReferenceListsAsync(OrderItem[] items,CancellationToken ct)
    {
        var eligible=items.Where(i=>!i.Order!.IsInventory).Select(i=>new{i.OrderId,List=i.SalesListId??i.Order!.SalesListId}).Where(i=>i.List.HasValue).ToArray();
        var orderIds=eligible.Select(i=>i.OrderId).Distinct().ToArray();
        var billed=await _db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&orderIds.Contains(i.OrderId)&&(i.Status==InvoiceStatus.Issued||i.Status==InvoiceStatus.Paid)).Select(i=>i.OrderId).Distinct().ToArrayAsync(ct);
        var ids=eligible.Where(i=>billed.Contains(i.OrderId)).Select(i=>i.List!.Value).Distinct().ToArray();
        return await _db.SalesLists.AsNoTracking().Where(l=>ids.Contains(l.Id)&&!l.IsDeleted&&!l.IsInventoryOffer&&l.Status!=SalesListStatus.Open&&l.Status!=SalesListStatus.Cancelled)
            .OrderBy(l=>l.StablePublicCode??l.PublicCode).ToArrayAsync(ct);
    }
    private async Task SendShippingCompletedListMenuAsync(long chat,ShippingReferenceContext context,OrderItem[] items,CancellationToken ct)
    {
        var lists=await ShippingReferenceListsAsync(items,ct);
        if(lists.Length==0){await ShippingReferenceTextAsync(chat,"برای این آیتم‌های آماده، لیست تکمیل‌شدهٔ فاکتورشده ثبت نشده است؛ موجودی مستقل لیست تکمیل‌شده ندارد.",ct);return;}
        var batches=lists.Skip(context.Offset).Chunk(50).ToArray();
        for(var index=0;index<batches.Length;index++)
        {
            var buttons=new List<IReadOnlyCollection<TelegramInlineButton>>();var tokens=new List<Guid>();
            foreach(var list in batches[index])
            {
                var token=await SaveShippingReferenceAsync(context with{Kind="list",SalesListId=list.Id,MessageId=null,Installed=false,Offset=0},ct);tokens.Add(token);
                var name=string.Join(" | ",new[]{list.PersianName?.Trim(),list.EnglishName?.Trim()}
                    .Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase));
                var duplicate=lists.Count(l=>l.DisplayCode==list.DisplayCode)>1?" — "+FormatShippingPersianDate(list.OpenDate):"";
                buttons.Add([new($"{list.DisplayCode} — {name}{duplicate}",ShippingReferencePrefix+"list:"+token.ToString("N"))]);
            }
            var title="📋 لیست تکمیل‌شدهٔ موردنظر را انتخاب کنید:"+(batches.Length>1?$"\nبخش {index+1} از {batches.Length} — همهٔ گزینه‌ها خودکار نمایش داده می‌شوند.":"");
            var sent=await SendShippingReferenceResultAsync(()=>_sender.SendInlineKeyboardAsync(chat.ToString(),title,buttons,ct),ct);
            if(sent.IsSuccessful)foreach(var token in tokens)await BindShippingReferenceAsync(token,sent.MessageId,ct);
        }
    }
    private async Task SendShippingSelectedCompletedListAsync(long chat,ShippingReferenceContext context,OrderItem[] items,CancellationToken ct)
    {
        var list=(await ShippingReferenceListsAsync(items,ct)).SingleOrDefault(l=>l.Id==context.SalesListId)
            ??throw new InvalidOperationException("این لیست دیگر به اقلام آمادهٔ همین بسته مربوط نیست.");
        var requests=await _db.SalesListRequests.AsNoTracking().Include(r=>r.Bottle).Where(r=>!r.IsDeleted&&r.SalesListId==list.Id&&r.Status!=SalesListRequestStatus.Cancelled&&r.Status!=SalesListRequestStatus.Expired&&r.Status!=SalesListRequestStatus.PendingConfirmation)
            .OrderBy(r=>r.ConfirmedAt).ThenBy(r=>r.CreatedAt).ThenBy(r=>r.Id).ToArrayAsync(ct);
        if(requests.Length==0)throw new InvalidOperationException("فهرست معتبر تکمیل این لیست ثبت نشده است.");
        var pages=FormatChannelSalesListPages(list,requests);var caption="✅ لیست فروش تکمیل شد\n\n"+pages.Main;
        var sent=!string.IsNullOrWhiteSpace(list.TelegramPhotoFileId)?await SendShippingReferenceResultAsync(()=>_sender.SendPhotoHtmlAsync(chat.ToString(),list.TelegramPhotoFileId,caption,ct),ct):new TelegramSendResult(false);
        if(!sent.IsSuccessful)foreach(var part in SplitTelegramMessage(caption))await SendShippingReferenceResultAsync(()=>_sender.SendHtmlAsync(chat.ToString(),part,ct),ct);
        if(!string.IsNullOrWhiteSpace(pages.Continuation))foreach(var part in SplitTelegramMessage(pages.Continuation))await SendShippingReferenceResultAsync(()=>_sender.SendHtmlAsync(chat.ToString(),part,ct),ct);
    }
    private async Task<IReadOnlyCollection<TelegramInlineButton>[]> ExistingShippingReferenceButtonsAsync(string chat,long messageId,CancellationToken ct)
    {
        var rows=await _db.NotificationOutbox.AsNoTracking().Where(n=>!n.IsDeleted&&n.Channel==ShippingReferenceChannel&&n.EventType=="AddressReferences"&&n.Recipient==chat)
            .OrderByDescending(n=>n.CreatedAt).ToArrayAsync(ct);
        var row=rows.FirstOrDefault(n=>JsonSerializer.Deserialize<ShippingReferenceContext>(n.Payload) is {Installed:true} c&&c.MessageId==messageId);
        return row is null?[]:ShippingReferenceButtons(row.Id);
    }
    private async Task UpgradeShippingAddressReferencesAsync(TelegramCallbackQuery callback,CancellationToken ct)
    {
        var message=callback.Message!;
        if(message.ReplyMarkup is null||message.From?.IsBot!=true||message.Chat.Id.ToString()!=_options.ShippingChatId.Trim()||
            (message.ReplyMarkup.Rows.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith(ShippingReferencePrefix,StringComparison.Ordinal)==true)&&
             !message.ReplyMarkup.Rows.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith("shipping:idreport:",StringComparison.Ordinal)==true)))return;
        if(!long.TryParse(_options.BotToken.Split(':')[0],out var bot)||message.From.Id!=bot||!await IsAuthorizedShippingAdminAsync(message.Chat.Id,callback.From.Id,ct))return;
        try
        {
            Guid? request=null;Guid? customerId=null;
            foreach(var button in message.ReplyMarkup.Rows.SelectMany(r=>r))
            {
                var data=button.CallbackData??"";var parts=data.Split(':');
                if(parts.Length!=3||parts[0]!="shipping"||!Guid.TryParseExact(parts[2],"N",out var id))continue;
                if(parts[1] is "idreport" or "tracking" or "trackingbatch" or "sent" or "select")request=id;
                else if(parts[1]=="trackingcustomer")customerId=id;
            }
            if(!request.HasValue&&!customerId.HasValue)return;
            Customer customer;
            if(request.HasValue)customer=await ShippingPackageService.ValidatePackageAsync(_db,request.Value,null,ct);
            else customer=await _db.Customers.AsNoTracking().SingleAsync(c=>c.Id==customerId&&!c.IsDeleted,ct);
            var source=request.HasValue?SharedShippingService.Items(_db,request.Value):CustomerShippingItems(customer);
            var items=await source.AsNoTracking().Where(i=>i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&i.ShippedAt==null&&
                (!request.HasValue||i.ShippingRequestId==request)).Select(i=>i.Id).ToArrayAsync(ct);
            var alreadyInstalled=message.ReplyMarkup.Rows.SelectMany(r=>r).Any(b=>b.CallbackData?.StartsWith(ShippingReferencePrefix,StringComparison.Ordinal)==true);
            var token=alreadyInstalled?Guid.Empty:await SaveShippingReferenceAsync(new(customer.Id,request,items,message.Chat.Id.ToString(),MessageId:message.MessageId),ct);
            var old=message.ReplyMarkup.Rows.Select(r=>(IReadOnlyCollection<TelegramInlineButton>)r.Where(b=>b.CallbackData?.StartsWith("shipping:idreport:",StringComparison.Ordinal)!=true)
                .Select(b=>new TelegramInlineButton(b.Text,b.CallbackData,b.CopyText?.Text,b.Url,b.Style)).ToArray()).Where(r=>r.Count>0).ToArray();
            var updated=alreadyInstalled?old:old.Concat(ShippingReferenceButtons(token)).ToArray();
            var sent=await _sender.EditReplyMarkupAsync(message.Chat.Id.ToString(),message.MessageId,updated,ct);
            if(sent.IsSuccessful&&token!=Guid.Empty)await BindShippingReferenceAsync(token,message.MessageId,ct);
        }
        catch(InvalidOperationException){/* The original operation still performs its own strict review check. */}
    }

}
