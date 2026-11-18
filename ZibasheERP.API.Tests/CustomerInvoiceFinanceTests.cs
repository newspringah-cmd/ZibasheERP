using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;
using ZibasheERP.Infrastructure.Services;

public static class CustomerInvoiceFinanceTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var connection=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var guard=new SqlConnectionStringBuilder(connection);if(guard.InitialCatalog!="ZibasheERPCloudDev"||guard.DataSource!="127.0.0.1,14333")throw new Exception("Wrong database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        var marker="finance"+Guid.NewGuid().ToString("N")[..14];const long owner=741471928;
        var customer=new Customer{Id=Guid.NewGuid(),Username=marker,TelegramId="801"+Random.Shared.NextInt64(100000000,999999999),FullName="Finance fixture",Mobile=Guid.NewGuid().ToString("N")[..18],CurrentDebt=999000000,CreatedAt=DateTime.UtcNow};
        var other=new Customer{Id=Guid.NewGuid(),Username=marker+"other",TelegramId="802"+Random.Shared.NextInt64(100000000,999999999),FullName="Other fixture",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=DateTime.UtcNow};
        var orders=new List<Guid>();db.Customers.AddRange(customer,other);await db.SaveChangesAsync();
        async Task<(Order Order,Invoice Invoice)> Add(decimal amount,InvoiceStatus status,bool inventory=false,bool manual=false,bool deleted=false,bool cancelledOrder=false,bool deletedOrder=false,Guid? buyer=null)
        {
            var order=new Order{Id=Guid.NewGuid(),CustomerId=buyer??customer.Id,OrderNumber="FIN-"+Guid.NewGuid().ToString("N"),Status=cancelledOrder?OrderStatus.Cancelled:OrderStatus.Invoiced,Source=manual?OrderSource.ManualInvoice:OrderSource.SalesListInvoice,IsInventory=inventory,IsDeleted=deletedOrder,CreatedAt=DateTime.UtcNow};
            var invoice=new Invoice{Id=Guid.NewGuid(),OrderId=order.Id,InvoiceNumber="FIN-"+Guid.NewGuid().ToString("N"),Status=status,TotalAmount=amount,IsDeleted=deleted,CreatedAt=DateTime.UtcNow};
            orders.Add(order.Id);db.Orders.Add(order);db.Invoices.Add(invoice);await db.SaveChangesAsync();return(order,invoice);
        }
        var sender=DispatchProxy.Create<ITelegramMessageSender,CustomerFinanceTestSender>();var sent=(CustomerFinanceTestSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var f in typeof(TelegramWebhookController).GetFields(BindingFlags.Instance|BindingFlags.NonPublic))if(f.Name.EndsWith("Drafts")&&f.FieldType.GetConstructor(Type.EmptyTypes)!=null)f.SetValue(controller,Activator.CreateInstance(f.FieldType));
        typeof(TelegramWebhookController).GetField("_db",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,db);
        typeof(TelegramWebhookController).GetField("_sender",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,sender);
        typeof(TelegramWebhookController).GetField("_options",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,new TelegramOptions{OwnerUserId=owner.ToString()});
        Task Callback(long user,long chat,string type,string action="start")=>(Task)typeof(TelegramWebhookController).GetMethod("HandleCustomerFinanceCallbackAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[new TelegramCallbackQuery("fixture",new(user,null),new TelegramMessage(null,new(chat,type),new(user,null),null,MessageId:1),"invoiceadmin:customer-finance:"+action),CancellationToken.None])!;
        Task<bool> Input(string text,long user=owner)=>(Task<bool>)typeof(TelegramWebhookController).GetMethod("TryHandleCustomerFinanceMessageAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[new TelegramMessage(text,new(user,"private"),new(user,null),null,MessageId:2),CancellationToken.None])!;
        try
        {
            await Add(1000000,InvoiceStatus.Paid);var inventory=await Add(2000000,InvoiceStatus.Paid,true);
            var partial=await Add(600000,InvoiceStatus.Issued);var manual=await Add(400000,InvoiceStatus.Issued,manual:true);
            await Add(9000000,InvoiceStatus.Cancelled);await Add(8000000,InvoiceStatus.Draft);await Add(7000000,InvoiceStatus.Paid,deleted:true);
            await Add(6000000,InvoiceStatus.Issued,cancelledOrder:true);await Add(5000000,InvoiceStatus.Paid,deletedOrder:true);await Add(9000000,InvoiceStatus.Paid,buyer:other.Id);
            foreach(var p in new[]{new Payment{Id=Guid.NewGuid(),OrderId=partial.Order.Id,Amount=150000,Status=PaymentStatus.Confirmed},new Payment{Id=Guid.NewGuid(),OrderId=partial.Order.Id,Amount=100000,Status=PaymentStatus.Confirmed,IsDeleted=true},new Payment{Id=Guid.NewGuid(),OrderId=partial.Order.Id,Amount=50000,Status=PaymentStatus.Refunded},new Payment{Id=Guid.NewGuid(),OrderId=manual.Order.Id,Amount=399000,Status=PaymentStatus.Pending}})db.Payments.Add(p);
            await db.SaveChangesAsync();db.ChangeTracker.Clear();var service=new CustomerInvoiceFinanceService(db);
            var report=(await service.GetAsync(" @"+marker.ToUpperInvariant()+" "))!;
            check(report.PaidCount==2&&report.PaidTotal==3000000,"customer finance sums paid invoices from both regular sales and ready inventory exactly once");
            check(report.PendingCount==2&&report.PendingTotal==1000000&&report.Outstanding==850000&&report.PartialPayments==150000,"customer debt subtracts only confirmed undeleted partial payments and includes manual invoices");
            check(report.Outstanding==(await new RegistrationDebtService(db).GetAsync(customer.TelegramId,marker)).OutstandingToman,"financial report debt agrees with the debt used to authorize customer registration");
            check((await service.GetAsync(customer.TelegramId!))!.CustomerId==customer.Id,"numeric Telegram ID resolves the same stable financial account");
            check(await service.GetAsync("unknown_"+marker) is null,"unknown customer does not silently produce a misleading zero-balance report");
            await db.Orders.Where(o=>o.Id==inventory.Order.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.CustomerId,other.Id));db.ChangeTracker.Clear();
            check((await service.GetAsync(marker))!.PaidTotal==1000000&&(await service.GetAsync(other.Username!))!.PaidTotal==11000000,"transferred invoice belongs only to its current customer in financial reports");
            await db.Orders.Where(o=>o.Id==inventory.Order.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.CustomerId,customer.Id));db.ChangeTracker.Clear();
            await db.Customers.Where(c=>c.Id==other.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Username,"@"+marker.ToUpperInvariant()));db.ChangeTracker.Clear();
            var ambiguous=false;try{await service.GetAsync(marker);}catch(InvalidOperationException){ambiguous=true;}
            check(ambiguous,"duplicate normalized usernames are rejected instead of combining two customer balances");
            await db.Customers.Where(c=>c.Id==other.Id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.Username,marker+"other"));db.ChangeTracker.Clear();
            var overpayment=await db.Payments.SingleAsync(p=>p.OrderId==partial.Order.Id&&p.Status==PaymentStatus.Confirmed&&!p.IsDeleted);overpayment.Amount=750000;await db.SaveChangesAsync();db.ChangeTracker.Clear();
            check((await service.GetAsync(marker))!.Outstanding==400000&&(await service.GetAsync(marker))!.PartialPayments==750000,"overpayment never produces negative invoice debt and its actual confirmed amount remains visible");
            await db.Payments.Where(p=>p.Id==overpayment.Id).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.Amount,150000));db.ChangeTracker.Clear();
            Task Menu(long chat,long user)=>(Task)typeof(TelegramWebhookController).GetMethod("SendInvoiceAdminSectionAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,[chat,"invoices",user,CancellationToken.None,null])!;
            await Menu(owner,owner);check(sent.Keyboards.Last().Any(b=>b.CallbackData=="invoiceadmin:customer-finance:start"),"customer financial report is available inside the manager invoice menu");
            await Menu(owner+1,owner+1);await Menu(-100123,owner);check(sent.Keyboards.TakeLast(2).All(rows=>rows.All(b=>b.CallbackData!="invoiceadmin:customer-finance:start")),"accountant and group invoice menus do not expose the customer finance option");
            var before=sent.Messages.Count;await Callback(owner+1,owner+1,"private");await Callback(owner,-100123,"supergroup");await Callback(owner,owner+1,"private");
            check(sent.Messages.Count==before&&sent.Alerts.Count(a=>a.Contains("فقط برای مدیر"))==3,"accountants, group callbacks and mismatched private chat IDs cannot open customer financial reports");
            await Callback(owner,owner,"private");check(sent.Messages.Last().Contains("آیدی مشتری"),"manager finance action explicitly asks for a customer identity");
            await Input("unknown_"+marker);check(sent.Messages.Last().Contains("پیدا نشد"),"unknown identity keeps the manager prompt available for correction");
            await Input("@"+marker);check(sent.Messages.Last().Contains("3,000,000")&&sent.Messages.Last().Contains("850,000")&&sent.Messages.Last().Contains("150,000")&&sent.Messages.Last().Contains("تمام سوابق"),"manager receives paid invoice totals and remaining debt with partial-payment explanation in the same private chat");
            check(!await Input("@"+marker),"completed financial report consumes its input draft");
            await Callback(owner,owner,"private");await Callback(owner,owner,"private","cancel");check(!await Input("@"+marker),"cancelled report cannot consume later manager messages");
            var invoiceCount=await db.Invoices.CountAsync(i=>orders.Contains(i.OrderId));
            check(invoiceCount==10&&(await db.Customers.AsNoTracking().SingleAsync(c=>c.Id==customer.Id)).CurrentDebt==999000000,"financial reporting does not alter invoices, payments or the stored customer ledger");
        }
        finally
        {
            await Callback(owner,owner,"private","cancel");db.ChangeTracker.Clear();
            await db.Payments.Where(p=>orders.Contains(p.OrderId)).ExecuteDeleteAsync();await db.Invoices.Where(i=>orders.Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Contains(o.Id)).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id||c.Id==other.Id).ExecuteDeleteAsync();
        }
    }
}
public class CustomerFinanceTestSender:DispatchProxy
{
    public List<string> Messages=[];public List<string> Alerts=[];public List<TelegramInlineButton[]> Keyboards=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="AnswerCallbackAsync"){Alerts.Add(args![1] as string??"");return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name is "SendInlineKeyboardAsync" or "SendReplyAsync" or "SendAsync")
        {Messages.Add((string)args![1]!);if(method.Name=="SendInlineKeyboardAsync")Keyboards.Add(((IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args[2]!).SelectMany(r=>r).ToArray());return Task.FromResult(new TelegramSendResult(true,MessageId:1));}
        throw new NotSupportedException(method.Name);
    }
}
