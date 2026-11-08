using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class ShippingPackageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var customers=Enumerable.Range(0,2).Select(_=>new Customer{Id=Guid.NewGuid(),FullName="Package fixture",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow}).ToArray();
        db.Customers.AddRange(customers);await db.SaveChangesAsync();
        var orders=new List<Guid>();var since=DateTime.UtcNow.AddHours(-1);var package=Guid.NewGuid();
        async Task<OrderItem> Item(int customer,bool inventory,OrderItemFulfillmentStatus status,Guid? request=null,DateTime? billedAt=null)
        {
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customers[customer].Id,OrderNumber="PACK-"+Guid.NewGuid().ToString("N"),IsInventory=inventory,Status=OrderStatus.Invoiced,CreatedAt=DateTime.UtcNow,InvoiceIssuedAt=billedAt??DateTime.UtcNow};
            orders.Add(order.Id);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,CreatedAt=DateTime.UtcNow,RequestedVolumeMl=inventory?1.5m:3m,FulfillmentStatus=status,ShippingRequestId=request,ShippingRequestedAt=request.HasValue?since:null};
            db.Orders.Add(order);db.OrderItems.Add(item);db.Invoices.Add(new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber="PACKINV-"+Guid.NewGuid().ToString("N"),Status=ZibasheERP.Domain.Enums.InvoiceStatus.Issued,IssuedAt=billedAt??DateTime.UtcNow});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();return item;
        }
        async Task<OrderItem> Reload(Guid id)=>await db.OrderItems.AsNoTracking().Include(i=>i.Order).SingleAsync(i=>i.Id==id);
        try
        {
            var anchor=await Item(0,false,OrderItemFulfillmentStatus.DecantedReadyToShip,package);
            var inventory=await Item(0,true,OrderItemFulfillmentStatus.DecantedReadyToShip);
            var normal=await Item(0,false,OrderItemFulfillmentStatus.WaitingForArrivalInIran);
            var other=await Item(1,true,OrderItemFulfillmentStatus.DecantedReadyToShip);
            var older=await Item(0,true,OrderItemFulfillmentStatus.DecantedReadyToShip,billedAt:since.AddHours(-1));
            await ShippingPackageService.AttachInventoryAsync(db,customers[0].Id);db.ChangeTracker.Clear();
            var attached=await Reload(inventory.Id);
            check(attached.ShippingRequestId==package&&attached.ShippingRequestedAt==since&&attached.ShippedAt==null&&attached.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip,
                "a newly invoiced ready inventory package automatically joins the sole open parcel without marking it shipped");
            check((await Reload(normal.Id)).ShippingRequestId==null&&(await Reload(other.Id)).ShippingRequestId==null&&(await Reload(older.Id)).ShippingRequestId==null,
                "automatic parcel additions exclude unready normal perfume, other customers and inventory invoiced before the address request");
            // A stale association must not override the actual fulfillment state.
            var unready=await Item(0,false,OrderItemFulfillmentStatus.WaitingForArrivalInIran,package);
            var unreadyOrder=await db.Orders.SingleAsync(o=>o.Id==unready.OrderId);
            var sibling=new OrderItem{Id=Guid.NewGuid(),OrderId=unreadyOrder.Id,CreatedAt=DateTime.UtcNow,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,ShippingRequestId=package,ShippingRequestedAt=since};
            db.OrderItems.Add(sibling);await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var catchup=await Item(0,true,OrderItemFulfillmentStatus.DecantedReadyToShip);
            await ShippingPackageService.CompleteAsync(db,package,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(catchup.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped,
                "dispatch atomically catches up ready inventory billed before deployment or between earlier membership checks");
            check((await Reload(anchor.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped&&(await Reload(inventory.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped,
                "dispatch ships the original parcel and its late ready inventory additions");
            check((await Reload(unready.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.WaitingForArrivalInIran&&(await Reload(unready.Id)).Order!.Status!=OrderStatus.Shipped&&
                (await Reload(normal.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.WaitingForArrivalInIran,
                "unready items are never shipped even with a stale parcel association and partially fulfilled orders remain unshipped");
            var later=await Item(0,true,OrderItemFulfillmentStatus.DecantedReadyToShip);
            await ShippingPackageService.AttachInventoryAsync(db,customers[0].Id);
            await ShippingPackageService.CompleteAsync(db,package,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(later.Id)).ShippingRequestId==null&&(await Reload(later.Id)).ShippedAt==null,
                "inventory invoiced after dispatch cannot join a closed parcel when tracking is sent or replayed later");
            var addressOnly=await ShippingPackageService.CompleteAsync(db,null,customers[1].Id);db.ChangeTracker.Clear();
            check(addressOnly.Length==0&&(await Reload(other.Id)).ShippedAt==null,
                "address-only tracking never marks every unassigned ready perfume as shipped");
            var mismatch=false;try{await ShippingPackageService.CompleteAsync(db,package,customers[1].Id);}catch(InvalidOperationException){mismatch=true;}
            check(mismatch,"tracking cannot complete another customer's parcel");
            var p1=Guid.NewGuid();var p2=Guid.NewGuid();
            await Item(1,false,OrderItemFulfillmentStatus.DecantedReadyToShip,p1);
            check((await ShippingPackageService.CompleteAsync(db,null,customers[1].Id)).Length==0&&(await Reload(other.Id)).ShippedAt==null,
                "a customer-only tracking button cannot ship a newer parcel even when only one parcel is currently open");
            await Item(1,false,OrderItemFulfillmentStatus.DecantedReadyToShip,p2);
            await ShippingPackageService.AttachInventoryAsync(db,customers[1].Id);db.ChangeTracker.Clear();
            check((await Reload(other.Id)).ShippingRequestId==null&&(await ShippingPackageService.CompleteAsync(db,null,customers[1].Id)).Length==0,
                "multiple open parcels require a specific parcel instead of assigning inventory or tracking by guesswork");
            // Isolate new gift parcels from the older fixture's still-open parcels.
            await db.OrderItems.Where(i => orders.Contains(i.OrderId)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ShippingRequestId, (Guid?)null));
            db.ChangeTracker.Clear();
            var giverParcel=Guid.NewGuid();var receiverParcel=Guid.NewGuid();
            await Item(0,false,OrderItemFulfillmentStatus.DecantedReadyToShip,giverParcel);
            await Item(1,false,OrderItemFulfillmentStatus.DecantedReadyToShip,receiverParcel);
            async Task<OrderItem> Gift(Guid? parcel=null)
            {
                var gift=await Item(0,true,OrderItemFulfillmentStatus.DecantedReadyToShip,parcel);
                await db.Orders.Where(o=>o.Id==gift.OrderId).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Source,OrderSource.ManualInvoice)
                    .SetProperty(o=>o.Notes,"فاکتور دستی | هدیه برای recipient-fixture"));
                db.NotificationOutbox.Add(new NotificationOutbox{Id=Guid.NewGuid(),OrderId=gift.OrderId,CustomerId=customers[1].Id,
                    Channel="Telegram",EventType="GiftInvoiceIssued",Recipient="fixture",Payload="{}",Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});
                await db.SaveChangesAsync();db.ChangeTracker.Clear();return gift;
            }
            var manualGift=await Gift();var oldWrongGift=await Gift(giverParcel);
            await ShippingPackageService.AttachInventoryAsync(db,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(manualGift.Id)).ShippingRequestId==null,"outgoing inventory gifts never auto-attach to the donor's open package");
            await ShippingPackageService.AttachInventoryAsync(db,customers[1].Id);db.ChangeTracker.Clear();
            check((await Reload(manualGift.Id)).ShippingRequestId==receiverParcel,
                "inventory gift automatically joins the actual recipient's sole open parcel, not the donor's malformed old parcel");
            var payerReport=await ShippingPackageService.CustomerItems(db,customers[0]).Where(i=>i.ShippingRequestId==giverParcel).Select(i=>i.Id).ToArrayAsync();
            check(!payerReport.Contains(oldWrongGift.Id),"existing wrongly attached gifts are omitted from the donor's shipping report");
            var blocked=false;
            try { await ShippingPackageService.CompleteAsync(db,giverParcel,customers[0].Id); }
            catch(ShippingRecipientValidationException exception) { blocked=exception.ItemIds.Contains(oldWrongGift.Id); }
            db.ChangeTracker.Clear();
            check(blocked&&!await db.OrderItems.AnyAsync(i=>i.ShippingRequestId==giverParcel&&i.ShippedAt!=null),
                "a malformed gift parcel is blocked atomically before any item is shipped instead of silently omitting a mismatched gift");
            await db.OrderItems.Where(i=>i.Id==oldWrongGift.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippingRequestId,(Guid?)null));
            await ShippingPackageService.CompleteAsync(db,giverParcel,customers[0].Id);db.ChangeTracker.Clear();
            check((await Reload(oldWrongGift.Id)).ShippedAt==null&&(await Reload(manualGift.Id)).ShippedAt==null,
                "sending the donor's tracking code cannot mark outgoing gifts as shipped");
            var recipientDispatch=await ShippingPackageService.CompleteAsync(db,receiverParcel,customers[1].Id);db.ChangeTracker.Clear();
            check(recipientDispatch.Any(i=>i.Id==manualGift.Id)&&(await Reload(manualGift.Id)).FulfillmentStatus==OrderItemFulfillmentStatus.Shipped,
                "mixed payer invoices in a recipient parcel dispatch correctly using the physical recipient");
            var giftOnlyParcel=Guid.NewGuid();var giftOnly=await Gift(giftOnlyParcel);
            check((await ShippingPackageService.PackageCustomerAsync(db,giftOnlyParcel)).Id==customers[1].Id,
                "gift-only parcel resolves its owner from the confirmed recipient receipt rather than the payer");
            foreach(var state in new[]{ShippingPackApprovalState.Sending,ShippingPackApprovalState.AwaitingCustomer,ShippingPackApprovalState.RevisionRequested,ShippingPackApprovalState.NeedsReview})
            {
                var unapprovedParcel=Guid.NewGuid();var unapprovedItem=await Item(0,false,OrderItemFulfillmentStatus.DecantedReadyToShip,unapprovedParcel);
                var receiptId=Guid.NewGuid();
                db.NotificationOutbox.Add(new(){Id=receiptId,CustomerId=customers[0].Id,Channel=ShippingPackApproval.ReceiptChannel,EventType="PackApproval",Recipient=unapprovedParcel.ToString("N"),
                    Payload=JsonSerializer.Serialize(new ShippingPackApprovalData{CustomerId=customers[0].Id,RequestId=unapprovedParcel,ItemIds=[unapprovedItem.Id],State=state}),Status=NotificationOutboxStatus.Processed,CreatedAt=DateTime.UtcNow});
                await db.SaveChangesAsync();db.ChangeTracker.Clear();
                check((await ShippingPackageService.ValidateDispatchAsync(db,unapprovedParcel,customers[0].Id)).Id==customers[0].Id,
                    state+" customer photo feedback does not block tracking for the verified recipient");
                await ShippingPackageService.CompleteAsync(db,unapprovedParcel,customers[0].Id);db.ChangeTracker.Clear();
                var closed=JsonSerializer.Deserialize<ShippingPackApprovalData>((await db.NotificationOutbox.AsNoTracking().SingleAsync(n=>n.Id==receiptId)).Payload)!;
                check(closed.State==ShippingPackApprovalState.Dispatched&&closed.DispatchedAt.HasValue&&closed.ConfirmedAt==null&&closed.ConfirmedBy==null&&(await Reload(unapprovedItem.Id)).ShippedAt.HasValue,
                    state+" parcel completion closes its photo flow without falsely recording customer approval");
            }
        }
        finally
        {
            db.ChangeTracker.Clear();var customerIds=customers.Select(c=>c.Id).ToArray();await db.NotificationOutbox.Where(n=>(n.OrderId!=null&&orders.Contains(n.OrderId.Value))||(n.Channel==ShippingPackApproval.ReceiptChannel&&customerIds.Contains(n.CustomerId))).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();
            await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            var ids=customers.Select(c=>c.Id).ToArray();await db.Customers.Where(c=>ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
