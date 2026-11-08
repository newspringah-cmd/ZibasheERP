using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class GiftParcelMembershipTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if(string.IsNullOrWhiteSpace(connection))return;
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated development database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var stamp=Guid.NewGuid().ToString("N")[..12];var now=DateTime.UtcNow;
        var customers=Enumerable.Range(0,4).Select(n=>new Customer{Id=Guid.NewGuid(),Username="giftparcel_"+stamp+n,
            TelegramId=(880000000L+Random.Shared.Next(10000000)+n).ToString(),FullName="Synthetic gift parcel",Mobile=stamp+n,CurrentDebt=123000+n,CreatedAt=now}).ToArray();
        var orders=new List<Guid>();var lists=new List<Guid>();var batches=new List<Guid>();
        var perfume=new Perfume{Id=Guid.NewGuid(),Name="Synthetic gift parcel",CreatedAt=now};
        var address=new Address{Id=Guid.NewGuid(),CustomerId=customers[0].Id,ReceiverName="Synthetic donor",Mobile="09120000000",City="تهران",FullAddress="Synthetic only",PostalCode="0123456789",CreatedAt=now};
        db.Customers.AddRange(customers);db.Addresses.Add(address);db.Perfumes.Add(perfume);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        async Task<OrderItem> Item(int payer,Guid? parcel,int? recipient=null,bool manual=false,decimal volume=2)
        {
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customers[payer].Id,OrderNumber="GIFT-PARCEL-"+Guid.NewGuid().ToString("N"),
                Status=OrderStatus.Invoiced,Source=manual?OrderSource.ManualInvoice:OrderSource.SalesListInvoice,IsInventory=manual,
                Notes=manual?"فاکتور دستی | هدیه برای "+customers[recipient!.Value].Username:null,FinalAmount=250000,CreatedAt=now};
            orders.Add(order.Id);db.Orders.Add(order);
            var item=new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,RequestedVolumeMl=volume,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip,
                ShippingRequestId=parcel,ShippingRequestedAt=parcel.HasValue?now:null,ShippingTelegramMessageId=parcel.HasValue?1234:null,CreatedAt=now};
            if(recipient.HasValue&&!manual)
            {
                var list=new SalesList{Id=Guid.NewGuid(),PerfumeId=perfume.Id,PersianName="Synthetic gift",EnglishName="Synthetic",CreatedAt=now};lists.Add(list.Id);db.SalesLists.Add(list);
                var source=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=customers[payer].TelegramId!,TelegramUsername=customers[payer].Username,
                    IsGift=true,GiftRecipientTelegramUsername=customers[recipient.Value].Username,Status=SalesListRequestStatus.Invoiced,VolumeMl=volume,CreatedAt=now,ExpiresAt=now,ExternalReference="giftparcel:"+Guid.NewGuid().ToString("N")};
                db.SalesListRequests.Add(source);item.SourceSalesListRequestId=source.Id;item.SalesListId=list.Id;
            }
            db.OrderItems.Add(item);db.Invoices.Add(new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber="GIFT-PARCEL-"+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Paid,TotalAmount=250000,IssuedAt=now});
            if(recipient.HasValue)db.NotificationOutbox.Add(new(){Id=Guid.NewGuid(),OrderId=order.Id,CustomerId=customers[recipient.Value].Id,Channel="Telegram",EventType="GiftInvoiceIssued",Recipient="synthetic",Payload="{}",Status=NotificationOutboxStatus.Processed,CreatedAt=now});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();return item;
        }
        async Task<OrderItem> Reload(Guid id)=>await db.OrderItems.AsNoTracking().SingleAsync(i=>i.Id==id);
        try
        {
            var parcel=Guid.NewGuid();var anchor=await Item(0,parcel);var gift=await Item(0,parcel,1);var gift2=await Item(0,parcel,2,volume:5);var manual=await Item(0,parcel,1,true,1.5m);
            var incoming=await Item(1,parcel,0);var paidBefore=await db.Invoices.AsNoTracking().Where(i=>orders.Contains(i.OrderId)).Select(i=>new{i.Id,i.TotalAmount,i.Status}).OrderBy(i=>i.Id).ToArrayAsync();
            var debts=await db.Customers.AsNoTracking().Where(c=>customers.Select(x=>x.Id).Contains(c.Id)).OrderBy(c=>c.Id).Select(c=>c.CurrentDebt).ToArrayAsync();
            var review=await GiftShippingMembershipService.InspectAsync(db,parcel);
            check(review.Items.Length==3&&review.Items.Single(i=>i.ItemId==gift.Id).RecipientId==customers[1].Id,"gift parcel review identifies outgoing normal and manual stock gifts using their actual recipients");
            check((await Reload(gift.Id)).ShippingRequestId==parcel,"gift membership inspection is read-only");
            check(await GiftShippingMembershipService.RepairAsync(db,parcel)==3,"known outgoing gifts detach from the donor parcel without stopping its valid items");db.ChangeTracker.Clear();
            check((await ShippingPackageService.ValidatePackageAsync(db,parcel,customers[0].Id)).Id==customers[0].Id,"donor parcel validates after historical outgoing gift links are repaired");
            check((await Reload(anchor.Id)).ShippingRequestId==parcel&&(await Reload(incoming.Id)).ShippingRequestId==parcel,"own purchases and incoming gifts stay in the donor parcel");
            check(new[]{gift.Id,gift2.Id,manual.Id}.All(id=>db.OrderItems.AsNoTracking().Any(i=>i.Id==id&&i.ShippingRequestId==null&&i.ShippingRequestedAt==null&&i.ShippingTelegramMessageId==null&&i.ShippedAt==null&&i.FulfillmentStatus==OrderItemFulfillmentStatus.DecantedReadyToShip)),"repair clears only parcel association and keeps gifts ready for their own recipients");
            check(await GiftShippingMembershipService.RepairAsync(db,parcel)==0&&await db.NotificationOutbox.CountAsync(n=>n.Channel==GiftShippingMembershipService.AuditChannel&&n.Recipient==parcel.ToString())==1,"repeated gift repair is idempotent with one durable audit and no notification jobs");
            var paidAfter=await db.Invoices.AsNoTracking().Where(i=>orders.Contains(i.OrderId)).Select(i=>new{i.Id,i.TotalAmount,i.Status}).OrderBy(i=>i.Id).ToArrayAsync();
            var debtsAfter=await db.Customers.AsNoTracking().Where(c=>customers.Select(x=>x.Id).Contains(c.Id)).OrderBy(c=>c.Id).Select(c=>c.CurrentDebt).ToArrayAsync();
            check(paidBefore.SequenceEqual(paidAfter)&&debts.SequenceEqual(debtsAfter),"gift repair preserves paid invoices, payable amounts and customer debt");
            check(await ShippingPackageService.CustomerItems(db,customers[1]).AnyAsync(i=>i.Id==gift.Id)&&!await ShippingPackageService.CustomerItems(db,customers[0]).AnyAsync(i=>i.Id==gift.Id),"detached gift remains visible only in its actual recipient's shipping items");
            // A real shared parcel deliberately contains multiple physical owners.
            var shared=Guid.NewGuid();await Item(0,shared);var allowed=await Item(0,shared,1);var outside=await Item(0,shared,2);
            var data=new SharedShippingService.Parcel(shared,customers[0].Id,address.Id,[customers[0].Id,customers[1].Id],741471928,now);
            db.NotificationOutbox.Add(new(){Id=SharedShippingService.ReceiptId(shared),CustomerId=customers[0].Id,Channel=SharedShippingService.Channel,EventType="SharedShippingParcel",Recipient=shared.ToString(),Payload=JsonSerializer.Serialize(data),Status=NotificationOutboxStatus.Processed,CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check(await GiftShippingMembershipService.RepairAsync(db,shared)==1&&(await Reload(allowed.Id)).ShippingRequestId==shared&&(await Reload(outside.Id)).ShippingRequestId==null,"explicit shared-post members keep their gifts while gifts for nonmembers leave that parcel");
            check((await ShippingPackageService.ValidatePackageAsync(db,shared)).Id==customers[0].Id,"valid shared parcel still validates after excluding an outsider gift");
            // Duplicate recipient records are not silently chosen.
            var ambiguous=Guid.NewGuid();await Item(0,ambiguous);var disputed=await Item(0,ambiguous,1);
            var duplicate=new Customer{Id=Guid.NewGuid(),FullName="Synthetic duplicate",Username="@"+customers[1].Username,Mobile=stamp+"dup",CreatedAt=now};db.Customers.Add(duplicate);await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check(await GiftShippingMembershipService.RepairAsync(db,ambiguous)==0&&(await Reload(disputed.Id)).ShippingRequestId==ambiguous,"ambiguous duplicate gift identities require review and cannot be auto-detached");
            await db.Customers.Where(c=>c.Id==duplicate.Id).ExecuteDeleteAsync();
            var conflict=Guid.NewGuid();await Item(0,conflict);var conflicting=await Item(0,conflict,1);
            await db.SalesListRequests.Where(r=>r.Id==conflicting.SourceSalesListRequestId).ExecuteUpdateAsync(s=>s
                .SetProperty(r=>r.GiftRecipientTelegramUserId,customers[2].TelegramId));
            check(await GiftShippingMembershipService.RepairAsync(db,conflict)==0&&(await Reload(conflicting.Id)).ShippingRequestId==conflict,"conflicting stable ID and username are never resolved by guessing a gift recipient");
            var foreign=Guid.NewGuid();await Item(0,foreign);var foreignPurchase=await Item(1,foreign);
            check(await GiftShippingMembershipService.RepairAsync(db,foreign)==0&&(await Reload(foreignPurchase.Id)).ShippingRequestId==foreign,"an unrelated ordinary purchase is not disguised as an outgoing gift or silently removed");
            var onlyGift=Guid.NewGuid();var onlyGiftItem=await Item(0,onlyGift,1);
            check(await GiftShippingMembershipService.RepairAsync(db,onlyGift)==0&&(await Reload(onlyGiftItem.Id)).ShippingRequestId==onlyGift&&(await ShippingPackageService.ValidatePackageAsync(db,onlyGift)).Id==customers[1].Id,"a valid gift-only parcel belongs to its actual receiver and stays intact");
            var concurrent=Guid.NewGuid();await Item(0,concurrent);await Item(0,concurrent,1);
            async Task<int> RepairOtherContext()
            {
                await using var other=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
                return await GiftShippingMembershipService.RepairAsync(other,concurrent);
            }
            var parallel=await Task.WhenAll(RepairOtherContext(),RepairOtherContext());
            check(parallel.Sum()==1&&await db.NotificationOutbox.CountAsync(n=>n.Channel==GiftShippingMembershipService.AuditChannel&&n.Recipient==concurrent.ToString())==1,"concurrent operator repairs detach a gift exactly once and retain one audit receipt");
            var receiverInitiated=Guid.NewGuid();await Item(0,receiverInitiated);var awaitingReceiver=await Item(0,receiverInitiated,1);
            await GiftShippingMembershipService.RepairCustomerAsync(db,customers[1].Id);db.ChangeTracker.Clear();
            check((await Reload(awaitingReceiver.Id)).ShippingRequestId==null,"the receiver can request a gift without waiting for the donor to reopen an old incorrect parcel");
            // Closed parcels and queued tracking cannot be rewritten.
            var closed=Guid.NewGuid();var sentAnchor=await Item(0,closed);var closedGift=await Item(0,closed,1);
            await db.OrderItems.Where(i=>i.Id==sentAnchor.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ShippedAt,now).SetProperty(i=>i.FulfillmentStatus,OrderItemFulfillmentStatus.Shipped));
            check(await GiftShippingMembershipService.RepairAsync(db,closed)==0&&(await Reload(closedGift.Id)).ShippingRequestId==closed,"a parcel with any dispatched item is never rewritten by gift repair");
            var active=Guid.NewGuid();await Item(0,active);var activeGift=await Item(0,active,1);var batch=new TrackingImportBatch{Id=Guid.NewGuid(),SourceHash=stamp+"active",CreatedAt=now};batches.Add(batch.Id);db.TrackingImportBatches.Add(batch);
            db.TrackingDispatches.Add(new(){Id=Guid.NewGuid(),ImportBatchId=batch.Id,CustomerId=customers[0].Id,ShippingRequestId=active,Status=TrackingDispatchStatus.Ready,RecipientName="Synthetic",TrackingCode=stamp,CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check(await GiftShippingMembershipService.RepairAsync(db,active)==0&&(await Reload(activeGift.Id)).ShippingRequestId==active,"queued tracking freezes parcel membership before gift repair");
            var attempt=await db.TrackingDispatches.SingleAsync(d=>d.ImportBatchId==batch.Id);attempt.Status=TrackingDispatchStatus.Failed;
            db.TrackingDispatchDeliveries.Add(new(){Id=Guid.NewGuid(),TrackingDispatchId=attempt.Id,TelegramChatId="synthetic",AttemptedAt=now,CreatedAt=now});await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check(await GiftShippingMembershipService.RepairAsync(db,active)==0,"an uncertain Telegram delivery also freezes membership after the dispatch has failed");
            // Entry points use the same recipient policy for fresh assignments.
            var donorIds=await ShippingPackageService.CustomerItems(db,customers[0]).Where(i=>i.ShippingRequestId==null).Select(i=>i.Id).ToArrayAsync();
            check(!donorIds.Contains(gift.Id)&&!donorIds.Contains(gift2.Id)&&!donorIds.Contains(manual.Id),"fresh donor shipping requests exclude all outgoing gift types and volumes");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.TrackingDispatchDeliveries.Where(d=>batches.Contains(d.TrackingDispatch!.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingDispatches.Where(d=>batches.Contains(d.ImportBatchId)).ExecuteDeleteAsync();await db.TrackingImportBatches.Where(b=>batches.Contains(b.Id)).ExecuteDeleteAsync();
            var customerIds=customers.Select(c=>c.Id).ToArray();await db.NotificationOutbox.Where(n=>customerIds.Contains(n.CustomerId)).ExecuteDeleteAsync();
            await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.OrderItems.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(r=>lists.Contains(r.SalesListId)).ExecuteDeleteAsync();await db.SalesLists.Where(l=>lists.Contains(l.Id)).ExecuteDeleteAsync();await db.Perfumes.Where(p=>p.Id==perfume.Id).ExecuteDeleteAsync();await db.Addresses.Where(a=>a.Id==address.Id).ExecuteDeleteAsync();await db.Customers.Where(c=>customerIds.Contains(c.Id)||c.Mobile==stamp+"dup").ExecuteDeleteAsync();
        }
    }
}
