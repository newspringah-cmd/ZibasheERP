using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class InventoryReferencePricingTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");if(string.IsNullOrWhiteSpace(connection))return;
        var target=new SqlConnectionStringBuilder(connection);if(target.DataSource!="127.0.0.1,14333"||target.InitialCatalog!="ZibasheERPCloudDev")throw new InvalidOperationException("Only the isolated development database is allowed");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="pricefix_"+Guid.NewGuid().ToString("N")[..16];var perfume=await db.Perfumes.Select(v=>v.Id).FirstAsync();
        var bottle=new Bottle{Id=Guid.NewGuid(),Name=marker,VolumeMl=3,SalePrice=2000,IsActive=true,CreatedAt=DateTime.UtcNow};db.Bottles.Add(bottle);
        SalesList Source(int code)=>new(){Id=Guid.NewGuid(),PerfumeId=perfume,PublicCode=code,EnglishName=marker,PersianName="مرجع قیمت",PricePerMl=10000,TotalVolume=100,OpenDate=DateTime.UtcNow,CreatedAt=DateTime.UtcNow,
            TelegramChannelId="-1001236168661",TelegramMessageId=Random.Shared.Next(30000000,40000000),TelegramPhotoFileId="fixture-photo"};
        var original=Source(Random.Shared.Next(7000000,7500000));var unrelated=Source(Random.Shared.Next(7500000,8000000));db.SalesLists.AddRange(original,unrelated);await db.SaveChangesAsync();
        var ids=new List<Guid>{original.Id,unrelated.Id};var manualPerfumes=new List<Guid>();var service=new ReadyInventoryService(db);
        async Task<SalesList> Stock(Guid? reference,int volume=3)
        {var id=Guid.NewGuid();ids.Add(id);var item=await service.CreateAsync(new(id,reference,marker,"fixture-photo",8000,volume,bottle.Id,2000,"shelf",771234567));if(reference is null)manualPerfumes.Add(item.PerfumeId);return item;}
        async Task<SalesListRequest> Pending(SalesList stock,string user,decimal price)
        {var r=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=stock.Id,VolumeMl=3,BottleId=bottle.Id,BottlePrice=2000,PerfumePricePerMl=price,TelegramUserId=marker+user,TelegramUsername=marker+user,
            CreatedAt=DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddMinutes(10),ExternalReference=marker+user};db.SalesListRequests.Add(r);await db.SaveChangesAsync();return r;}
        try
        {
            var available=await Stock(original.Id);var held=await Stock(original.Id);var manual=await Stock(null);var other=await Stock(unrelated.Id);var sold=await Stock(original.Id);
            check(available.InventoryReferencePricePerMl==10000&&available.PricePerMl==8000,"new linked inventory stores reference baseline separately from a custom package price");
            await db.SalesLists.Where(v=>v.Id==available.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.InventoryReferencePricePerMl,(decimal?)null));
            var migration=new ZibasheERP.Infrastructure.Migrations.AddInventoryReferencePricing();
            var initialization=migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Single().Sql;
            await db.Database.ExecuteSqlRawAsync(initialization);db.ChangeTracker.Clear();
            var initialized=await db.SalesLists.SingleAsync(v=>v.Id==available.Id);
            check(initialized.InventoryReferencePricePerMl==10000&&initialized.PricePerMl==8000,
                "migration initializes legacy reference baseline without retroactively changing an inventory price");
            var pending=await Pending(available,"pending",8000);var accepted=await Pending(held,"accepted",8000);
            await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(accepted.Id,accepted.TelegramUserId);
            db.ChangeTracker.Clear();await db.SalesLists.Where(v=>v.Id==sold.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.Status,SalesListStatus.Invoiced));
            await db.SalesLists.Where(v=>v.Id==original.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.PricePerMl,12000m));
            await InventoryReferencePricing.SynchronizeAsync(db);db.ChangeTracker.Clear();
            var updated=await db.SalesLists.SingleAsync(v=>v.Id==available.Id);var reserved=await db.SalesLists.SingleAsync(v=>v.Id==held.Id);
            check(updated.PricePerMl==10000&&updated.InventoryReferencePricePerMl==12000&&updated.FixedBottlePrice==2000,
                "a reference increase adds the same per-ml difference once while preserving package markup and bottle price");
            check(reserved.PricePerMl==10000&&reserved.Status==SalesListStatus.Full&&reserved.ReservedVolume==3&&
                (await db.SalesListRequests.SingleAsync(v=>v.Id==accepted.Id)).PerfumePricePerMl==8000,
                "reference increase never changes an already-confirmed customer price or releases its indefinite reservation");
            check((await db.SalesLists.SingleAsync(v=>v.Id==manual.Id)).PricePerMl==8000&&(await db.SalesLists.SingleAsync(v=>v.Id==other.Id)).PricePerMl==8000&&
                (await db.SalesLists.SingleAsync(v=>v.Id==sold.Id)).PricePerMl==8000,"manual inventory, other list codes and invoiced packages do not inherit unrelated reference price changes");
            var events=await db.NotificationOutbox.CountAsync(v=>v.Channel=="ReadyInventoryRefresh"&&v.EventType=="InventoryReferencePriceIncreased"&&v.Recipient==available.Id.ToString());
            await InventoryReferencePricing.SynchronizeAsync(db);db.ChangeTracker.Clear();
            check((await db.SalesLists.SingleAsync(v=>v.Id==available.Id)).PricePerMl==10000&&events==1&&
                await db.NotificationOutbox.CountAsync(v=>v.Channel=="ReadyInventoryRefresh"&&v.EventType=="InventoryReferencePriceIncreased"&&v.Recipient==available.Id.ToString())==1,
                "price synchronization retries cannot add the same increase or channel refresh twice");
            var stale=false;try{await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(pending.Id,pending.TelegramUserId);}catch(InvalidOperationException e)when(e.Message.Contains("قیمت")){stale=true;}
            check(stale,"unconfirmed old-price selection must show the updated amount before customer confirmation");
            db.ChangeTracker.Clear();
            var next=Source((await db.SalesLists.MaxAsync(v=>v.PublicCode))+100);next.StablePublicCode=original.PublicCode;next.PricePerMl=14000;next.OpenDate=DateTime.UtcNow.AddMinutes(1);ids.Add(next.Id);db.SalesLists.Add(next);await db.SaveChangesAsync();
            await InventoryReferencePricing.SynchronizeAsync(db);db.ChangeTracker.Clear();
            updated=await db.SalesLists.SingleAsync(v=>v.Id==available.Id);
            check(updated.PricePerMl==12000&&updated.InventoryReferenceListId==original.Id&&updated.InventoryReferencePricePerMl==14000,
                "linked inventory follows the newest published bottle cycle of its original stable list code");
            await db.SalesLists.Where(v=>v.Id==next.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.PricePerMl,15000m));
            var quote=await FlashSalePricing.QuoteAsync(db,updated,DateTime.UtcNow);db.ChangeTracker.Clear();
            check(quote.PricePerMl==13000&&(await db.SalesLists.SingleAsync(v=>v.Id==available.Id)).PricePerMl==13000,
                "customer quote catches a reference increase immediately even before the background worker runs");
            await db.SalesLists.Where(v=>v.Id==next.Id).ExecuteUpdateAsync(v=>v.SetProperty(r=>r.PricePerMl,14000m));
            await InventoryReferencePricing.SynchronizeAsync(db);db.ChangeTracker.Clear();
            check((await db.SalesLists.SingleAsync(v=>v.Id==available.Id)).PricePerMl==13000,"a reference price decrease does not silently reduce the ready inventory price");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(v=>v.Channel=="ReadyInventoryRefresh"&&ids.Select(id=>id.ToString()).Contains(v.Recipient)).ExecuteDeleteAsync();
            await db.SalesListRequests.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();await db.InventoryPositionChanges.Where(v=>ids.Contains(v.SalesListId)).ExecuteDeleteAsync();
            await db.SalesLists.Where(v=>ids.Contains(v.Id)).ExecuteDeleteAsync();await db.Perfumes.Where(v=>manualPerfumes.Contains(v.Id)).ExecuteDeleteAsync();await db.Bottles.Where(v=>v.Id==bottle.Id).ExecuteDeleteAsync();
        }
    }
}
