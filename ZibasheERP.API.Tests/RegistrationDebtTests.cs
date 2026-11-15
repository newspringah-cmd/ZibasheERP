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
using ZibasheERP.Infrastructure.Repositories;
using ZibasheERP.Infrastructure.Services;

public static class RegistrationDebtTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var cs=Environment.GetEnvironmentVariable("TRACKING_TEST_DB_CONNECTION")??throw new Exception("Isolated SQL required");var guard=new SqlConnectionStringBuilder(cs);
        if(guard.InitialCatalog!="ZibasheERPCloudDev"||guard.DataSource!="127.0.0.1,14333")throw new Exception("Wrong database");
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        var now=DateTime.UtcNow;const long owner=741471928,user=80000012345,otherUser=80000012346;
        var name="debt"+Guid.NewGuid().ToString("N")[..12];
        var customer=new Customer{Id=Guid.NewGuid(),TelegramId=user.ToString(),Username=name,FullName="Debt fixture",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=now};
        var other=new Customer{Id=Guid.NewGuid(),TelegramId=otherUser.ToString(),Username="other"+Guid.NewGuid().ToString("N")[..10],FullName="Other debt fixture",Mobile=Guid.NewGuid().ToString("N")[..18],CreatedAt=now};
        var orders=Enumerable.Range(0,4).Select(i=>new Order{Id=Guid.NewGuid(),CustomerId=i==3?other.Id:customer.Id,OrderNumber="DEBT-"+Guid.NewGuid().ToString("N"),Source=i==2?OrderSource.ManualInvoice:OrderSource.SalesListInvoice,IsInventory=i==1,Status=OrderStatus.Invoiced,CreatedAt=now}).ToArray();
        var invoices=orders.Select((o,i)=>new Invoice{Id=Guid.NewGuid(),OrderId=o.Id,InvoiceNumber="DEBT-"+Guid.NewGuid().ToString("N"),Status=InvoiceStatus.Issued,TotalAmount=i==3?100000000: i==0?6000000:i==1?3000000:1000000,CreatedAt=now}).ToArray();
        var payment=new Payment{Id=Guid.NewGuid(),OrderId=orders[0].Id,Amount=1,Status=PaymentStatus.Pending,PaymentMethod="Test",CreatedAt=now};
        var perfume=await db.Perfumes.Select(p=>p.Id).FirstAsync();var bottle=await db.Bottles.Where(b=>!b.IsDeleted).Select(b=>b.Id).FirstAsync();
        var list=new SalesList{Id=Guid.NewGuid(),PublicCode=Random.Shared.Next(10000000,20000000),PerfumeId=perfume,EnglishName="Synthetic debt reservation",TotalVolume=100,PricePerMl=1000,Status=SalesListStatus.Open,CreatedAt=now,OpenDate=now};
        var request=new SalesListRequest{Id=Guid.NewGuid(),SalesListId=list.Id,TelegramUserId=user.ToString(),TelegramUsername=name,VolumeMl=1,BottleId=bottle,PerfumePricePerMl=1000,Status=SalesListRequestStatus.PendingConfirmation,ExpiresAt=now.AddHours(1),CreatedAt=now};
        var sender=DispatchProxy.Create<ITelegramMessageSender,RegistrationDebtSender>();var sent=(RegistrationDebtSender)(object)sender;
        var controller=(TelegramWebhookController)RuntimeHelpers.GetUninitializedObject(typeof(TelegramWebhookController));
        foreach(var (field,value) in new(string,object)[]{("_db",db),("_sender",sender),("_options",new TelegramOptions{OwnerUserId=owner.ToString(),AdminChatId="-1000000"}),("_adminActivityContext",new AdminActivityContext()),("_salesListRepository",new SalesListRepository(db)),("_salesListRequestRepository",new SalesListRequestRepository(db)),("_logger",NullLogger<TelegramWebhookController>.Instance)})typeof(TelegramWebhookController).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,value);
        var service=new RegistrationDebtService(db);
        async Task<T> Call<T>(string method,params object?[] args)=>await (Task<T>)typeof(TelegramWebhookController).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,args)!;
        try
        {
            db.Customers.AddRange(customer,other);db.Orders.AddRange(orders);db.Invoices.AddRange(invoices);db.Payments.Add(payment);db.SalesLists.Add(list);db.SalesListRequests.Add(request);await db.SaveChangesAsync();
            check(!(await service.GetAsync(user.ToString(),name)).BlocksRegistration,"exactly ten million toman is allowed across list, inventory and manual invoices");
            invoices[2].TotalAmount+=1;await db.SaveChangesAsync();
            check((await service.GetAsync(user.ToString(),name)).BlocksRegistration,"one toman above ten million blocks registration");
            check((await service.GetAsync("admin-username:"+name,"@"+name.ToUpperInvariant())).OutstandingToman==10000001,"admin username-only identity resolves the same outstanding ledger");
            check((await service.GetAsync(user.ToString(),other.Username)).CustomerId==customer.Id,"verified numeric Telegram identity takes priority over a conflicting username");
            payment.Status=PaymentStatus.Confirmed;await db.SaveChangesAsync();
            check(!(await service.GetAsync(user.ToString(),name)).BlocksRegistration,"confirmed partial payments reduce debt to the exact allowed threshold");
            payment.Status=PaymentStatus.Pending;await db.SaveChangesAsync();
            check((await service.GetAsync(user.ToString(),name)).BlocksRegistration,"unconfirmed payment receipts do not clear the debt restriction");
            invoices[2].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();
            check(!(await service.GetAsync(user.ToString(),name)).BlocksRegistration,"paid invoices no longer contribute to pending debt");
            foreach(var status in new[]{InvoiceStatus.Cancelled,InvoiceStatus.Draft})
            {invoices[2].Status=status;await db.SaveChangesAsync();check(!(await service.GetAsync(user.ToString(),name)).BlocksRegistration,"non-payable invoice excluded: "+status);}
            invoices[2].Status=InvoiceStatus.Issued;orders[2].IsDeleted=true;await db.SaveChangesAsync();
            check(!(await service.GetAsync(user.ToString(),name)).BlocksRegistration,"deleted orders do not leave stale debt restrictions");
            orders[2].IsDeleted=false;await db.SaveChangesAsync();
            var rejected=false;try{await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(request.Id,user.ToString());}catch(InvalidOperationException e)when(e.Message==RegistrationDebtService.CustomerMessage){rejected=true;}
            check(rejected&&(await db.SalesLists.AsNoTracking().SingleAsync(l=>l.Id==list.Id)).ReservedVolume==0,"repository rejects a pre-existing pending reservation without consuming stock when debt rises");
            var customerCallback=new TelegramCallbackQuery("customer-debt",new(user,name),new TelegramMessage(null,new(-100111,"channel"),new(user,name),null,MessageId:1),"sly:dummy");
            check(!await Call<bool>("AllowCallbackRegistrationDebtAsync",customerCallback,user.ToString(),name,CancellationToken.None,null)&&sent.Prompts.Count==0&&sent.Alerts.Last().Contains("به علت بدهی"),"customer receives a blocking popup and cannot obtain an administrator override prompt");
            var command=new TelegramMessage($"/nextbottle {list.PublicCode} | @{name} | 1",new(owner,"private"),new(owner,null),null,MessageId:22);
            check(await Call<bool>("TryHandleAdminCommandAsync",command,CancellationToken.None)&&!await db.SalesListRequests.AnyAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle),"administrator command is paused for explicit consent before creating a next-bottle item");
            var token=sent.Prompts.Last().Token;var prompt=sent.Prompts.Last().MessageId;
            TelegramCallbackQuery Approval(long actor,string action="yes",long? message=null)=>new("approval",new(actor,null),new TelegramMessage(null,new(actor,"private"),new(actor,null),null,MessageId:message??prompt),$"debtlimit:{action}:{token}");
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",Approval(user),CancellationToken.None);
            check(!await db.NotificationOutbox.AnyAsync(n=>n.Channel=="AdminDebtOverride"&&n.CustomerId==customer.Id),"another account cannot approve a forwarded or forged override button");
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",Approval(owner,message:prompt+1),CancellationToken.None);
            check(!await db.SalesListRequests.AnyAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle),"approval is bound to the exact private confirmation message");
            invoices[2].TotalAmount+=1;await db.SaveChangesAsync();
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",Approval(owner),CancellationToken.None);
            check(!await db.SalesListRequests.AnyAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle)&&sent.Prompts.Last().Token!=token,
                "debt increasing after the warning requires fresh explicit consent instead of using the smaller approved amount");
            token=sent.Prompts.Last().Token;prompt=sent.Prompts.Last().MessageId;
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",Approval(owner),CancellationToken.None);
            check(await db.SalesListRequests.CountAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle)==1&&await db.NotificationOutbox.AnyAsync(n=>n.Channel=="AdminDebtOverride"&&n.CustomerId==customer.Id),"explicit owner consent resumes the original registration once and retains an audit receipt");
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",Approval(owner),CancellationToken.None);
            check(await db.SalesListRequests.CountAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle)==1,"repeated approval cannot duplicate the registered item");
            await Call<bool>("TryHandleAdminCommandAsync",command with{MessageId=23},CancellationToken.None);
            var cancelled=sent.Prompts.Last();await Call<bool>("TryHandleRegistrationDebtApprovalAsync",new TelegramCallbackQuery("cancel",new(owner,null),new TelegramMessage(null,new(owner,"private"),new(owner,null),null,MessageId:cancelled.MessageId),"debtlimit:no:"+cancelled.Token),CancellationToken.None);
            check(await db.SalesListRequests.CountAsync(r=>r.SalesListId==list.Id&&r.Kind==SalesListRequestKind.NextBottle)==1,"declining an override leaves reservations unchanged");
            var drafts=new TelegramAdminRequestDraftStore();
            typeof(TelegramWebhookController).GetField("_adminRequestDrafts",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,drafts);
            var edit=new TelegramAdminRequestDraft{ChatId=owner,UserId=owner,Kind=TelegramAdminRequestKind.CustomRequest,Stage=TelegramAdminRequestStage.AwaitingConfirmation,SalesListId=list.Id,Identity="@"+name,VolumeMl=1};drafts.Set(edit);
            var mutable=new TelegramCallbackQuery("mutable",new(owner,null),new TelegramMessage(null,new(owner,"private"),new(owner,null),null,MessageId:35),"adminrequest:confirm");
            var operation=await Call<string?>("CurrentDebtOperationAsync",mutable,CancellationToken.None);
            check(!await Call<bool>("AllowCallbackRegistrationDebtAsync",mutable,"admin-username:"+name,name,CancellationToken.None,operation),"interactive administrator item also requires explicit private debt consent");
            var stale=sent.Prompts.Last();var auditBefore=await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminDebtOverride"&&n.CustomerId==customer.Id);
            drafts.Set(new TelegramAdminRequestDraft{ChatId=owner,UserId=owner,Kind=TelegramAdminRequestKind.RemoveCustomerRequests,Stage=TelegramAdminRequestStage.AwaitingConfirmation,SalesListId=list.Id,Identity="@"+name,VolumeMl=1});
            await Call<bool>("TryHandleRegistrationDebtApprovalAsync",new TelegramCallbackQuery("stale",new(owner,null),new TelegramMessage(null,new(owner,"private"),new(owner,null),null,MessageId:stale.MessageId),"debtlimit:yes:"+stale.Token),CancellationToken.None);
            check(sent.Alerts.Last().Contains("تغییر")&&await db.NotificationOutbox.CountAsync(n=>n.Channel=="AdminDebtOverride"&&n.CustomerId==customer.Id)==auditBefore&&await db.SalesListRequests.CountAsync(r=>r.SalesListId==list.Id)==2,
                "changing an item draft into another operation invalidates the old approval before any registration or removal can run");
            invoices[2].Status=InvoiceStatus.Paid;await db.SaveChangesAsync();await new SalesListRequestRepository(db).ConfirmCurrentBottleAsync(request.Id,user.ToString());
            check((await db.SalesListRequests.AsNoTracking().SingleAsync(r=>r.Id==request.Id)).Status==SalesListRequestStatus.Confirmed,"customer registration becomes available immediately after debt falls below the threshold");
        }
        finally
        {
            db.ChangeTracker.Clear();await db.NotificationOutbox.Where(n=>n.Channel=="AdminDebtOverride"&&n.CustomerId==customer.Id).ExecuteDeleteAsync();await db.SalesListRequests.Where(r=>r.SalesListId==list.Id).ExecuteDeleteAsync();await db.SalesLists.Where(l=>l.Id==list.Id).ExecuteDeleteAsync();await db.Payments.Where(p=>p.Id==payment.Id).ExecuteDeleteAsync();await db.Invoices.Where(i=>orders.Select(o=>o.Id).Contains(i.OrderId)).ExecuteDeleteAsync();await db.Orders.Where(o=>orders.Select(v=>v.Id).Contains(o.Id)).ExecuteDeleteAsync();await db.Customers.Where(c=>c.Id==customer.Id||c.Id==other.Id).ExecuteDeleteAsync();
        }
    }
}
public class RegistrationDebtSender:DispatchProxy
{
    public List<(string Token,long MessageId)> Prompts=[];public List<string> Alerts=[];private long number=100;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="IsChatAdministratorAsync")return Task.FromResult(false);
        if(method.Name=="SendInlineKeyboardAsync")
        {
            var rows=(IReadOnlyCollection<IReadOnlyCollection<TelegramInlineButton>>)args![2]!;
            var yes=rows.SelectMany(r=>r).FirstOrDefault(b=>b.CallbackData?.StartsWith("debtlimit:yes:")==true);
            var id=++number;if(yes is not null)Prompts.Add((yes.CallbackData!["debtlimit:yes:".Length..],id));return Task.FromResult(new TelegramSendResult(true,MessageId:id));
        }
        if(method.Name=="AnswerCallbackAsync"){Alerts.Add(args![1] as string??"");return Task.FromResult(new TelegramSendResult(true));}
        if(method.Name is "SendReplyAsync" or "EditTextWithKeyboardAsync" or "SendAsync")return Task.FromResult(new TelegramSendResult(true,MessageId:++number));
        throw new NotSupportedException(method.Name);
    }
}
