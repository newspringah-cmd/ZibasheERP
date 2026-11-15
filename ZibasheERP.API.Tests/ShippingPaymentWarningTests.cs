using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.API.Controllers;
using ZibasheERP.API.Telegram;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

public static class ShippingPaymentWarningTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var cs=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");
        var target=new SqlConnectionStringBuilder(cs);if(target.InitialCatalog!="ZibasheERPCloudDev"||target.DataSource!="127.0.0.1,14333")throw new Exception("Wrong database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        const long owner=741471928,accountant=1907823600,outsider=999999111,chat=-1009998877;
        var now=DateTime.UtcNow;
        Customer Customer()=>new(){Id=Guid.NewGuid(),FullName="Synthetic unpaid shipping",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=now};
        var customer=Customer();var other=Customer();
        var orders=Enumerable.Range(0,4).Select(i=>new Order{Id=Guid.NewGuid(),CustomerId=i==3?other.Id:customer.Id,OrderNumber="WARN-"+Guid.NewGuid().ToString("N"),Source=i==2?OrderSource.ManualInvoice:OrderSource.SalesListInvoice,IsInventory=i==1,Status=OrderStatus.Invoiced,CreatedAt=now}).ToArray();
        var invoices=orders.Select(o=>new Invoice{Id=Guid.NewGuid(),OrderId=o.Id,InvoiceNumber="WARN-"+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Paid,TotalAmount=100000,CreatedAt=now}).ToArray();
        var links=new[]{new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=customer.Id,ChatId=chat.ToString(),Title="Synthetic shared group",IsActive=true,IsPrimaryForShipping=true,CreatedAt=now},new CustomerTelegramGroup{Id=Guid.NewGuid(),CustomerId=other.Id,ChatId=chat.ToString(),Title="Synthetic shared group",IsActive=true,CreatedAt=now}};
        var sender=DispatchProxy.Create<ITelegramMessageSender,ShippingPaymentWarningSender>();var transport=(ShippingPaymentWarningSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (name,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),ShippingOperatorUserIds=accountant.ToString()})})typeof(TelegramWebhookController).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(controller,value);
        async Task<(string? Text,bool Alert)> Answer(Guid? id,long actor=owner,string? success=null)
        {
            var cb=new TelegramCallbackQuery("warning-check",new(actor,null),new TelegramMessage(null,new(chat,"supergroup"),new(actor,null),null,MessageId:41),"shipping:preparedispatch");
            await (Task)typeof(TelegramWebhookController).GetMethod("AnswerShippingPreparationCallbackAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,new object?[]{cb,id,CancellationToken.None,success})!;
            return transport.Answers.Last();
        }
        try
        {
            db.Customers.AddRange(customer,other);db.CustomerTelegramGroups.AddRange(links);db.Orders.AddRange(orders);db.Invoices.AddRange(invoices);await db.SaveChangesAsync();
            check((await Answer(customer.Id)).Text is null,"fully paid customer starts address preparation without a debt warning");
            for(var index=0;index<3;index++)
            {
                invoices[index].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();var result=await Answer(customer.Id);
                check(result.Alert&&result.Text!.Contains("فاکتور پرداخت‌نشده")&&result.Text.Contains("پیش از ارسال پرداخت شود")&&result.Text.Length<=200,
                    "address preparation privately warns about unpaid "+(index==0?"list":index==1?"inventory":"manual")+" invoices, including items not ready for this parcel");
                invoices[index].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();
            }
            invoices[0].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();
            check((await Answer(customer.Id,accountant)).Alert,"configured accountant receives the same unpaid-invoice popup as the owner");
            check((await Answer(customer.Id,outsider)).Text is null,"an unauthorized caller cannot learn a customer's unpaid-invoice state");
            foreach(var status in new[]{InvoiceStatus.Draft,InvoiceStatus.Cancelled})
            {invoices[0].Status=status;await db.SaveChangesAsync();check((await Answer(customer.Id)).Text is null,"non-issued invoice does not produce a debt warning: "+status);}
            invoices[0].Status=InvoiceStatus.Issued;invoices[0].IsDeleted=true;await db.SaveChangesAsync();
            check((await Answer(customer.Id)).Text is null,"deleted invoices do not block or warn during address preparation");
            invoices[0].IsDeleted=false;orders[0].Status=OrderStatus.Cancelled;await db.SaveChangesAsync();
            check((await Answer(customer.Id)).Text is null,"cancelled orders are excluded from the debt warning");
            orders[0].Status=OrderStatus.Invoiced;orders[0].IsDeleted=true;await db.SaveChangesAsync();
            check((await Answer(customer.Id)).Text is null,"deleted orders do not leak stale debt warnings");
            orders[0].IsDeleted=false;invoices[0].Status=InvoiceStatus.Paid;invoices[3].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();
            check((await Answer(customer.Id)).Text is null&&!(await Answer(null)).Alert,"another customer's unpaid invoice in the same group does not warn for the selected primary customer");
            check((await Answer(other.Id)).Alert,"explicitly selecting the other group customer checks that customer's invoices");
            links[0].IsPrimaryForShipping=false;await db.SaveChangesAsync();
            check((await Answer(null)).Text is null,"ambiguous multi-customer groups wait for customer selection instead of guessing whose invoice is unpaid");
            invoices[0].Status=InvoiceStatus.Issued;await db.SaveChangesAsync();
            check((await Answer(customer.Id,owner,"آدرس ثبت شد ✅")).Alert,"a new unpaid invoice added after preparation started is detected at final address dispatch");
            invoices[0].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();
            check((await Answer(customer.Id,owner,"آدرس ثبت شد ✅")).Text=="آدرس ثبت شد ✅","payment completed during address entry clears the warning without a stale cached result");
            check(!db.ChangeTracker.HasChanges()&&await db.Invoices.CountAsync(i=>invoices.Select(v=>v.Id).Contains(i.Id))==4,
                "warning checks neither mutate payment records nor create an address, parcel or customer notification");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.Invoices.Where(i=>orders.Select(o=>o.Id).Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Select(v=>v.Id).Contains(o.Id)).ExecuteDeleteAsync();await db.CustomerTelegramGroups.Where(g=>links.Select(v=>v.Id).Contains(g.Id)).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id||c.Id==other.Id).ExecuteDeleteAsync();
        }
    }
}
public class ShippingPaymentWarningSender:DispatchProxy
{
    public List<(string? Text,bool Alert)> Answers=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name!="AnswerCallbackAsync")throw new Exception("Debt warning must not send public group messages: "+method.Name);
        Answers.Add(((string?)args![1],(bool)args[3]!));return Task.FromResult(new TelegramSendResult(true));
    }
}
