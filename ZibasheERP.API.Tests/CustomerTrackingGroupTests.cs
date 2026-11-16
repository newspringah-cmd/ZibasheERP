using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class CustomerTrackingGroupTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var cs=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var target=new SqlConnectionStringBuilder(cs);if(target.InitialCatalog!="ZibasheERPCloudDev"||target.DataSource!="127.0.0.1,14333")throw new Exception("Wrong database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        var now=DateTime.UtcNow;var stamp=Guid.NewGuid().ToString("N")[..12];
        var customers=Enumerable.Range(0,2).Select(n=>new Customer{Id=Guid.NewGuid(),FullName="Grouped tracking fixture",Username="tracking_"+stamp+n,TelegramId=(880000000L+Random.Shared.Next(10000000)+n).ToString(),Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=now}).ToArray();
        var orders=new List<Guid>();var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=Guid.NewGuid().ToString("N"),CreatedAt=now};
        var listId=Guid.NewGuid();var requestId=Guid.NewGuid();
        async Task<OrderItem> Add(int owner,Guid parcel,OrderItemFulfillmentStatus status,bool gift=false)
        {
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customers[owner].Id,OrderNumber="TRACK-GROUP-"+Guid.NewGuid().ToString("N"),Status=OrderStatus.Invoiced,CreatedAt=now};orders.Add(order.Id);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,ShippingRequestId=parcel,ShippingRequestedAt=now,CreatedAt=now,FulfillmentStatus=status,RequestedVolumeMl=5,SourceSalesListRequestId=gift?requestId:null,ShippedAt=status==OrderItemFulfillmentStatus.Shipped?now.AddDays(-1):null};
            db.Orders.Add(order);db.OrderItems.Add(item);await db.SaveChangesAsync();db.ChangeTracker.Clear();return item;
        }
        async Task<OrderItem> Reload(Guid id)=>await db.OrderItems.AsNoTracking().SingleAsync(i=>i.Id==id);
        var first=Guid.NewGuid();var second=Guid.NewGuid();var other=Guid.NewGuid();
        try
        {
            db.Customers.AddRange(customers);db.TrackingImportBatches.Add(batch);
            var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();
            db.SalesLists.Add(new SalesList{Id=listId,PublicCode=Random.Shared.Next(10000000,20000000),PerfumeId=perfume,EnglishName="Gift isolation fixture",TotalVolume=100,PricePerMl=1000,Status=SalesListStatus.Open,CreatedAt=now});
            db.SalesListRequests.Add(new SalesListRequest{Id=requestId,SalesListId=listId,TelegramUserId=customers[0].TelegramId!,TelegramUsername=customers[0].Username,IsGift=true,GiftRecipientTelegramUserId=customers[1].TelegramId,GiftRecipientTelegramUsername=customers[1].Username,VolumeMl=5,Status=SalesListRequestStatus.Confirmed,ExpiresAt=DateTime.MaxValue,ExternalReference="tracking-gift:"+stamp,CreatedAt=now});
            await db.SaveChangesAsync();
            var a=await Add(0,first,OrderItemFulfillmentStatus.DecantedReadyToShip);
            var b=await Add(0,second,OrderItemFulfillmentStatus.DecantedReadyToShip);
            var unready=await Add(0,first,OrderItemFulfillmentStatus.WaitingForArrivalInIran);
            var foreign=await Add(1,other,OrderItemFulfillmentStatus.DecantedReadyToShip);
            var gift=await Add(0,Guid.NewGuid(),OrderItemFulfillmentStatus.DecantedReadyToShip,true);
            var old=await Add(0,Guid.NewGuid(),OrderItemFulfillmentStatus.Shipped);
            var plan=await ShippingPackageService.CustomerDispatchRequestsAsync(db,first,customers[0].Id);
            check(plan.ToHashSet().SetEquals([first,second]),"one customer tracking plan includes every open ready request while excluding another customer, a donated gift and already-shipped parcels");
            var refused=false;try{await ShippingPackageService.CompleteCustomerRequestsAsync(db,[first,other],customers[0].Id);}catch(InvalidOperationException){refused=true;}
            check(refused&&(await Reload(a.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip,"mixed-recipient tracking group is rejected before any parcel status changes");
            var dispatch=new TrackingDispatch{Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=customers[0].Id,ShippingRequestId=first,TrackingCode="synthetic-group-"+stamp,RecipientName="Fixture",Status=TrackingDispatchStatus.Ready,CreatedAt=now};db.TrackingDispatches.Add(dispatch);await db.SaveChangesAsync();
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));typeof(TelegramWebhookController).GetField("_db",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,db);
            async Task<Guid[]> Snapshot(TrackingDispatch d)=>await (Task<Guid[]>)typeof(TelegramWebhookController).GetMethod("CustomerTrackingRequestsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[d,CancellationToken.None])!;
            var snapshot=await Snapshot(dispatch);check(snapshot.ToHashSet().SetEquals(plan),"tracking freezes the complete request set before delivery");
            var late=await Add(0,Guid.NewGuid(),OrderItemFulfillmentStatus.DecantedReadyToShip);
            check((await Snapshot(dispatch)).ToHashSet().SetEquals(plan),"a retry cannot silently add a later parcel to an existing tracking snapshot");
            var duplicate=new TrackingDispatch{Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=customers[0].Id,ShippingRequestId=second,TrackingCode="second-group-"+stamp,RecipientName="Fixture",Status=TrackingDispatchStatus.Ready,CreatedAt=now};db.TrackingDispatches.Add(duplicate);await db.SaveChangesAsync();
            refused=false;try{await Snapshot(duplicate);}catch(InvalidOperationException){refused=true;}
            check(refused,"another code cannot claim a parcel already covered by the customer's first tracking operation");
            db.ChangeTracker.Clear();
            await ShippingPackageService.CompleteCustomerRequestsAsync(db,snapshot,customers[0].Id);
            check((await Reload(a.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped&&(await Reload(b.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped,"successful tracking marks the ready items of both customer requests shipped");
            check((await Reload(unready.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.WaitingForArrivalInIran&&(await Reload(foreign.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&(await Reload(gift.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip&&(await Reload(late.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip,"group completion preserves unready invoices, other recipients, gifts given away and later parcels");
            var shippedAt=(await Reload(a.Id)).ShippedAt;await ShippingPackageService.CompleteCustomerRequestsAsync(db,snapshot,customers[0].Id);
            check((await Reload(a.Id)).ShippedAt==shippedAt&&(await Reload(old.Id)).ShippedAt==old.ShippedAt,"tracking completion retry preserves the original shipment timestamps");
            check((await Snapshot(dispatch)).ToHashSet().SetEquals(plan),"the durable request snapshot survives completion and still supports safe retries");
        }
        finally
        {
            db.ChangeTracker.Clear();var owners=customers.Select(c=>c.Id).ToArray();
            await db.NotificationOutbox.Where(n=>owners.Contains(n.CustomerId)).ExecuteDeleteAsync();
            var dispatchIds=await db.TrackingDispatches.Where(d=>d.ImportBatchId==batch.Id).Select(d=>d.Id).ToArrayAsync();await db.TrackingDispatchDeliveries.Where(d=>dispatchIds.Contains(d.TrackingDispatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>d.ImportBatchId==batch.Id).ExecuteDeleteAsync();await db.TrackingImportBatches.Where(b=>b.Id==batch.Id).ExecuteDeleteAsync();
            await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();await db.SalesListRequests.Where(r=>r.SalesListId==listId).ExecuteDeleteAsync();await db.SalesLists.Where(l=>l.Id==listId).ExecuteDeleteAsync();await db.Customers.Where(c=>owners.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
