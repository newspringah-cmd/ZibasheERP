using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Application.Services;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class GiftShippingStatisticsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var cs=new SqlConnectionStringBuilder(connection);if(cs.InitialCatalog!="ZibasheERPCloudDev"||cs.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        const long owner=771234567;var marker="gs_"+Guid.NewGuid().ToString("N")[..12];
        var customers=Enumerable.Range(0,3).Select(n=>new Customer{Id=Guid.NewGuid(),FullName=marker+" person "+n,Username=marker+"_"+n,TelegramId=(8811000+n).ToString(),Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow}).ToArray();
        var links=customers.Select((c,n)=>new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=c.Id,ChatId=(-1007799000-n).ToString(),Title="Gift statistics fixture",IsActive=true,IsPrimaryForShipping=false,CreatedAt=DateTime.UtcNow,LinkedAt=DateTime.UtcNow}).ToArray();
        db.Customers.AddRange(customers);db.CustomerTelegramGroups.AddRange(links);await db.SaveChangesAsync();
        var perfume=await db.Perfumes.AsNoTracking().FirstAsync();var orders=new List<Guid>();var lists=new List<Guid>();var requests=new List<Guid>();
        async Task<OrderItem> Item(int payer,string name,OrderItemFulfillmentStatus status,int? recipient=null,bool usernameOnly=false)
        {
            var list=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(200000000,1000000000),PersianName=name,EnglishName=name,PerfumeId=perfume.Id,TotalVolume=10,ReservedVolume=10,PricePerMl=100000,Status=SalesListStatus.Invoiced,OpenDate=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,IsInventoryOffer=false};
            lists.Add(list.Id);db.SalesLists.Add(list);SalesListRequest? request=null;
            if(recipient.HasValue)
            {
                request=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=customers[payer].TelegramId!,TelegramUsername=customers[payer].Username,
                    IsGift=true,GiftRecipientTelegramUserId=usernameOnly?null:customers[recipient.Value].TelegramId,
                    GiftRecipientTelegramUsername=usernameOnly?" @"+customers[recipient.Value].Username!.ToUpperInvariant()+" ":customers[recipient.Value].Username,
                    VolumeMl=2,Status=SalesListRequestStatus.Invoiced,CreatedAt=DateTime.UtcNow,ConfirmedAt=DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddDays(1),ExternalReference=Guid.NewGuid().ToString("N")};
                requests.Add(request.Id);db.SalesListRequests.Add(request);
            }
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customers[payer].Id,OrderNumber=marker+Guid.NewGuid().ToString("N"),IsInventory=false,Source=OrderSource.SalesListInvoice,
                Status=status==OrderItemFulfillmentStatus.Shipped?OrderStatus.Shipped:OrderStatus.Invoiced,InvoiceIssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,FinalAmount=200000};
            orders.Add(order.Id);db.Orders.Add(order);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,SalesListId=list.Id,SourceSalesListRequestId=request?.Id,RequestedVolumeMl=2,LineTotal=200000,FulfillmentStatus=status,
                ShippedAt=status==OrderItemFulfillmentStatus.Shipped?DateTime.UtcNow:null,CreatedAt=DateTime.UtcNow};
            db.OrderItems.Add(item);db.Invoices.Add(new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber=marker+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Paid,TotalAmount=200000,IssuedAt=DateTime.UtcNow,CreatedAt=DateTime.UtcNow});
            await db.SaveChangesAsync();return item;
        }
        var sender=DispatchProxy.Create<ITelegramMessageSender,GiftStatisticsSender>();var sent=(GiftStatisticsSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString()}),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        async Task<string> Report(int person,string text,string method)
        {
            sent.Texts.Clear();var message=new TelegramMessage(text,new(long.Parse(links[person].ChatId),"supergroup"),new(owner,null),null,MessageId:10);
            check(await (Task<bool>)typeof(TelegramWebhookController).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[message,CancellationToken.None])!,"gift statistics report route accepts the authorized connected customer group");
            return string.Join("\n",sent.Texts);
        }
        try
        {
            const string ownReady="خرید شخصی آماده تست",ownShipped="خرید شخصی ارسال‌شده تست",giftReady="هدیه لیستی آماده تست",giftShipped="هدیه لیستی ارسال‌شده تست",receiverOwn="خرید گیرنده آماده تست",thirdGift="هدیه شخص سوم تست";
            await Item(0,ownReady,OrderItemFulfillmentStatus.DecantedReadyToShip);await Item(0,ownShipped,OrderItemFulfillmentStatus.Shipped);
            var readyGift=await Item(0,giftReady,OrderItemFulfillmentStatus.DecantedReadyToShip,1);
            await Item(0,giftShipped,OrderItemFulfillmentStatus.Shipped,1,true);await Item(1,receiverOwn,OrderItemFulfillmentStatus.DecantedReadyToShip);
            await Item(1,thirdGift,OrderItemFulfillmentStatus.DecantedReadyToShip,2);
            var giverReady=await Report(0,"/r","TryHandleGroupShippingStatusCommandAsync");
            check(giverReady.Contains(ownReady)&&!giverReady.Contains(giftReady)&&!giverReady.Contains(giftShipped)&&!giverReady.Contains(thirdGift),"noninventory outgoing gifts never appear in the giver's ready-to-ship report");
            var receiverReady=await Report(1,"/r","TryHandleGroupShippingStatusCommandAsync");
            check(receiverReady.Contains(giftReady)&&receiverReady.Contains(receiverOwn)&&!receiverReady.Contains(thirdGift)&&receiverReady.Contains("تعداد آیتم‌ها: 2"),"received ordinary-list gift is counted exactly once with the recipient's ready items");
            var giverSent=await Report(0,"/s","TryHandleGroupShippingStatusCommandAsync");var receiverSent=await Report(1,"/s","TryHandleGroupShippingStatusCommandAsync");
            check(giverSent.Contains(ownShipped)&&!giverSent.Contains(giftShipped)&&receiverSent.Contains(giftShipped)&&!receiverSent.Contains(ownShipped),"historical shipped gifts belong only to the gift recipient in /s, including normalized username-only recipients");
            var giverStatus=await Report(0,"زیبا وضعیت عطرها","TryHandleCustomerStatusQuestionAsync");
            var receiverStatus=await Report(1,"زیبا وضعیت عطرها","TryHandleCustomerStatusQuestionAsync");
            check(giverStatus.Contains(ownReady)&&!giverStatus.Contains(giftReady)&&!giverStatus.Contains(giftShipped),"customer perfume-status counts exclude all outgoing ordinary-list gifts");
            check(receiverStatus.Contains(giftReady)&&receiverStatus.Contains(giftShipped)&&!receiverStatus.Contains(thirdGift),"customer perfume-status counts include actual received gift order items rather than depending on the giver's account");
            var giftItem=await db.OrderItems.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Customer).Include(i=>i.SourceSalesListRequest).SingleAsync(i=>i.Id==readyGift.Id);
            var labels=await (Task<Dictionary<Guid,string>>)typeof(TelegramWebhookController).GetMethod("ShippingRecipientLabelsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new[]{giftItem},CancellationToken.None])!;
            check(labels[giftItem.Id].Contains(customers[1].Username!)&&!labels[giftItem.Id].Contains(customers[0].Username!),"administrator item-status cards show the physical gift recipient instead of the invoice payer");
            var oldGift=await db.SalesListRequests.AsNoTracking().SingleAsync(r=>r.Id==readyGift.SourceSalesListRequestId);
            await db.SalesListRequests.Where(r=>r.Id==oldGift.Id).ExecuteUpdateAsync(s=>s
                .SetProperty(r=>r.GiftRecipientTelegramUserId,customers[2].TelegramId)
                .SetProperty(r=>r.GiftRecipientTelegramUsername,customers[1].Username));
            var conflictedReceiver=await Report(1,"/r","TryHandleGroupShippingStatusCommandAsync");
            var conflictedOther=await Report(2,"/r","TryHandleGroupShippingStatusCommandAsync");
            check(!conflictedReceiver.Contains(giftReady)&&!conflictedOther.Contains(giftReady),
                "recipient identity changed after report generation cannot put the same ready gift in either conflicting customer's next report");
            var conflictedStatus=await Report(1,"زیبا وضعیت عطرها","TryHandleCustomerStatusQuestionAsync");
            check(!conflictedStatus.Contains(giftReady),"a rejected billed gift cannot reappear through the old channel-request fallback");
            var conflictedItem=await db.OrderItems.AsNoTracking().Include(i=>i.Order).ThenInclude(o=>o!.Customer).Include(i=>i.SourceSalesListRequest).SingleAsync(i=>i.Id==readyGift.Id);
            var conflictedLabels=await (Task<Dictionary<Guid,string>>)typeof(TelegramWebhookController).GetMethod("ShippingRecipientLabelsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[new[]{conflictedItem},CancellationToken.None])!;
            check(conflictedLabels[readyGift.Id].Contains("نیازمند بررسی"),"administrator cards explicitly flag conflicting recipients rather than printing a guessed username");
            await db.SalesListRequests.Where(r=>r.Id==oldGift.Id).ExecuteUpdateAsync(s=>s
                .SetProperty(r=>r.GiftRecipientTelegramUserId,oldGift.GiftRecipientTelegramUserId)
                .SetProperty(r=>r.GiftRecipientTelegramUsername,oldGift.GiftRecipientTelegramUsername));
            links[1].ChatId=links[0].ChatId;await db.SaveChangesAsync();
            var shared=await Report(0,"/r","TryHandleGroupShippingStatusCommandAsync");
            check(shared.Contains(ownReady)&&shared.Contains(giftReady)&&shared.Contains(receiverOwn)&&!shared.Contains(thirdGift)&&shared.Contains("تعداد آیتم‌ها: 3"),"shared accounting group deduplicates received gifts and keeps unrelated recipients outside the report");
            var sharedStatus=await Report(0,"زیبا وضعیت عطرها","TryHandleCustomerStatusQuestionAsync");
            check(sharedStatus.Contains(ownReady)&&sharedStatus.Contains(giftReady)&&!sharedStatus.Contains(thirdGift),"shared-group customer status uses the same recipient ownership without cross-customer leakage");
            check(await db.Orders.Where(o=>o.Id==readyGift.OrderId).AllAsync(o=>o.CustomerId==customers[0].Id)&&await db.Invoices.Where(i=>i.OrderId==readyGift.OrderId).AllAsync(i=>i.Status==InvoiceStatus.Paid&&i.TotalAmount==200000),"recipient statistics never change the payer, paid invoice or financial total");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>requests.Contains(r.Id)).ExecuteDeleteAsync();await db.SalesLists.Where(s=>lists.Contains(s.Id)).ExecuteDeleteAsync();
            var ids=customers.Select(c=>c.Id).ToArray();await db.CustomerTelegramGroups.Where(g=>ids.Contains(g.CustomerId)).ExecuteDeleteAsync();await db.Customers.Where(c=>ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
public class GiftStatisticsSender:DispatchProxy
{
    public List<string> Texts=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name is "SendHtmlAsync" or "SendReplyAsync" or "SendAsync" or "SendInlineKeyboardAsync"){Texts.Add((string)args![1]!);return Task.FromResult(new TelegramSendResult(true,MessageId:1));}
        if(method.Name=="IsChatAdministratorAsync")return Task.FromResult(true);
        throw new NotSupportedException(method.Name);
    }
}
