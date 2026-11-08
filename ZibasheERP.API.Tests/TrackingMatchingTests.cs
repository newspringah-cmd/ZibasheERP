using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.API.Tracking;
using ZibasheERP.Domain.Entities;

public static class TrackingMatchingTests
{
    private static readonly Type Candidate=typeof(TrackingImportService).GetNestedType("Candidate",BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name)=>typeof(TrackingImportService).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!;
    public static string Normalize(string name)=>(string)Method("NormalizeName").Invoke(null,[name])!;
    public static TrackingMatch? Select(string target,string destination,IEnumerable<object> candidates,bool exact=false)
    {
        var values=candidates.ToArray();var array=Array.CreateInstance(Candidate,values.Length);
        for(var i=0;i<values.Length;i++)array.SetValue(values[i],i);
        return (TrackingMatch?)Method("SelectUnique").Invoke(null,[target,destination,array,exact]);
    }
    public static object Row(Guid customer,string name,string raw="",string city="",Guid? request=null)=>
        Activator.CreateInstance(Candidate,[customer,request,name,city,raw,"مشتری @fixture",DateTime.UtcNow,request.HasValue])!;

    public static async Task Run(Action<bool,string> check)
    {
        var id=Guid.NewGuid();
        foreach(var (target,source) in new[]{("آیدا فتحی","ایدا فتحی"),("فتانه خویینی","فتانه خوئینی"),("نرگس عبداللهی","نرگس عبدالهی"),("افسانه شیبیانی","افسانه شیبانی"),("پریا مردان پور","پریا مردانپور"),("مانده کرمی","مائده کرمی"),("شلیر فتحی","بنام شلیرفتحی"),("بهزاد سرلک","تحويل گيرنده : بهزاد سرلك")})
        {
            var result=Select(target,"",[Row(id,"@fixture","تهران، خیابان نمونه، پلاک ۲\nکدپستی: 1111111111\n"+source+"09121111111")]);
            check(result?.CustomerId==id&&result.Status==TrackingDispatchStatus.Ready,
                "tracking identifies a complete receiver-name line despite Persian spelling/OCR and spacing differences: "+target);
        }
        check(Select("احمدی","",[Row(id,"@fixture","تهران، خیابان احمدیان، کوچه نمونه\nمریم احمدی\n09121111111")])==null,
            "a surname cannot match a street name or a fragment of another receiver's full name");
        check(Select("پریسا احمدی","",[Row(id,"@first","پریسا احمدی"),Row(Guid.NewGuid(),"@second","پریسا احمدی")])==null,
            "identical receiver names belonging to different customers remain under review");
        var second=Guid.NewGuid();
        check(Select("پریسا احمدی","رشت",[Row(id,"پریسا احمدی",city:"تهران"),Row(second,"پریسا احمدی",city:"رشت")])?.CustomerId==second,
            "an exact structured destination city can disambiguate otherwise identical full receiver names");
        check(Select("پریسا احمدی","رشت",[Row(id,"پروانه احمدی",city:"رشت")])==null,
            "destination agreement never upgrades a weak name similarity to automatic delivery");
        check(Select("پریسا احمدی","",[Row(id,"پریسا احمدی",request:Guid.NewGuid()),Row(id,"پریسا احمدی",request:Guid.NewGuid())]) is {Status:TrackingDispatchStatus.NeedsReview,ShippingRequestId:null},
            "matching a customer with multiple open parcels does not guess the shipping request");
        check(Select("نام گیرنده خوانده نشد","",[Row(id,"فاطمه محمدی")])==null,
            "unreadable PDF recipient placeholders cannot turn into automatic customer matches");
        check(Select("مانده کرمی","",[Row(id,"مائده کرمی")],exact:true)==null,
            "old addresses still require exact normalized name evidence rather than fuzzy OCR guesses");
        check(Select("ایدا فتحی","",[Row(id,"آیدا فتحی")],exact:true)?.CustomerId==id,
            "exact matching of old addresses accepts equivalent Persian letter forms");
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION");
        if(!string.IsNullOrWhiteSpace(connection))await ReevaluateSql(connection,check);
    }
    private static async Task ReevaluateSql(string connection,Action<bool,string> check)
    {
        var selector=new SqlConnectionStringBuilder(connection);
        if(selector.InitialCatalog!="ZibasheERPCloudDev"||selector.DataSource!="127.0.0.1,14333")throw new InvalidOperationException("Isolated database required");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var customer=new Customer{Id=Guid.NewGuid(),FullName="Matching fixture",TelegramId="88119940",Mobile=Guid.NewGuid().ToString("N")[..18]};
        var batch=new TrackingImportBatch{Id=Guid.NewGuid(),CreatedAt=DateTime.UtcNow,SourceHash=Guid.NewGuid().ToString("N"),ConfirmedAt=DateTime.UtcNow};
        var rows=Enumerable.Range(0,6).Select(index=>new TrackingDispatch{Id=Guid.NewGuid(),ImportBatchId=batch.Id,CreatedAt=DateTime.UtcNow.AddSeconds(index),Carrier=TrackingCarrier.IranPost,
            TrackingCode=Guid.NewGuid().ToString("N"),RecipientName=index is 1 or 2?"بسته آزمون":"آیدا فتحی",Status=TrackingDispatchStatus.NeedsReview}).ToArray();
        rows[3].MatchNotes="نام گیرنده یا اطمینان خواندن نیازمند بررسی است";
        rows[4].Deliveries.Add(new(){Id=Guid.NewGuid(),TelegramChatId="fixture-only",AttemptedAt=DateTime.UtcNow});
        rows[5].Status=TrackingDispatchStatus.Sent;rows[5].CustomerId=customer.Id;rows[5].SentAt=DateTime.UtcNow;
        db.Customers.Add(customer);db.TrackingImportBatches.Add(batch);db.TrackingDispatches.AddRange(rows);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        try
        {
            var address=new Address{Id=Guid.NewGuid(),CustomerId=customer.Id,ReceiverName="@fixture",Mobile="09120000000",City="قزوین",FullAddress="قزوین، خیابان نمونه\nفتانه خوئینی09120000000",CreatedAt=DateTime.UtcNow};
            var order=new Order{Id=Guid.NewGuid(),CustomerId=customer.Id,DeliveryAddressId=address.Id,OrderNumber="MATCH-"+Guid.NewGuid().ToString("N"),Status=OrderStatus.Invoiced};
            var closedParcel=Guid.NewGuid();
            db.Add(address);db.Add(order);
            db.OrderItems.AddRange(new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,ShippingRequestId=closedParcel,FulfillmentStatus=OrderItemFulfillmentStatus.Shipped,ShippedAt=DateTime.UtcNow},
                new OrderItem{Id=Guid.NewGuid(),OrderId=order.Id,ShippingRequestId=closedParcel,FulfillmentStatus=OrderItemFulfillmentStatus.DecantedReadyToShip});
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            using(var actual=new TrackingImportService(db,Microsoft.Extensions.Options.Options.Create(new ZibasheERP.API.AddressLabels.AddressLabelOptions{OpenAiApiKey="fixture"}),null!,Microsoft.Extensions.Logging.Abstractions.NullLogger<TrackingImportService>.Instance))
            {
                var matched=await actual.MatchAsync("فتانه خویینی","",default);
                check(matched.CustomerId==customer.Id&&matched.Status==TrackingDispatchStatus.Ready&&matched.ShippingRequestId==null,
                    "actual SQL matching resolves the Persian recipient line while excluding a parcel that already has shipped items");
            }
            var matcher=DispatchProxy.Create<ITrackingImportService,MatchingProxy>();
            ((MatchingProxy)(object)matcher).CustomerId=customer.Id;
            var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
            typeof(TelegramWebhookController).GetField("_db",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,db);
            typeof(TelegramWebhookController).GetField("_trackingImportService",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,matcher);
            var changed=await (Task<int>)typeof(TelegramWebhookController).GetMethod("ReevaluateTrackingBatchMatchesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,[batch.Id,CancellationToken.None])!;
            db.ChangeTracker.Clear();var saved=await db.TrackingDispatches.Where(v=>v.ImportBatchId==batch.Id).ToArrayAsync();
            check(changed==2&&saved.Single(v=>v.Id==rows[0].Id).Status==TrackingDispatchStatus.Ready,
                "group rematch upgrades confident unsent rows in a previously confirmed batch without sending messages");
            check(saved.Where(v=>v.Id==rows[1].Id||v.Id==rows[2].Id).Count(v=>v.Status==TrackingDispatchStatus.Ready)==1,
                "group rematch never attaches two tracking codes to the same shipping request");
            check(saved.Single(v=>v.Id==rows[3].Id).Status==TrackingDispatchStatus.NeedsReview&&saved.Single(v=>v.Id==rows[4].Id).Status==TrackingDispatchStatus.NeedsReview&&
                saved.Single(v=>v.Id==rows[5].Id).Status==TrackingDispatchStatus.Sent,
                "group rematch preserves low OCR confidence, unknown delivery attempts and already sent tracking codes");
            check(!await db.NotificationOutbox.AnyAsync(v=>v.CustomerId==customer.Id||saved.Select(s=>s.Id).Contains(v.Id)),
                "group rematch creates no tracking-send jobs and requires a separate explicit send confirmation");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.TrackingImportBatches.Where(v=>v.Id==batch.Id).ExecuteDeleteAsync();
            await db.OrderItems.Where(v=>v.Order!=null&&v.Order.CustomerId==customer.Id).ExecuteDeleteAsync();await db.Orders.Where(v=>v.CustomerId==customer.Id).ExecuteDeleteAsync();
            await db.Addresses.Where(v=>v.CustomerId==customer.Id).ExecuteDeleteAsync();await db.Customers.Where(v=>v.Id==customer.Id).ExecuteDeleteAsync();
        }
    }

}

public class MatchingProxy:DispatchProxy
{
    public Guid CustomerId;private readonly Guid PackageId=Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name=="MatchAsync"
        ?Task.FromResult(new TrackingMatch(CustomerId,args![0]?.ToString()=="بسته آزمون"?PackageId:null,TrackingDispatchStatus.Ready,"تطبیق آزمایشی"))
        :throw new InvalidOperationException("Unexpected external operation: "+method.Name);
}
