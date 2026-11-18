using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed record CustomerInvoiceFinance(Guid CustomerId,string Identity,int PaidCount,decimal PaidTotal,
    int PendingCount,decimal PendingTotal,decimal Outstanding,decimal PartialPayments);

public sealed class CustomerInvoiceFinanceService(AppDbContext db)
{
    public async Task<CustomerInvoiceFinance?> GetAsync(string identity,CancellationToken ct=default)
    {
        var input=identity.Trim();var numeric=!input.StartsWith('@')&&long.TryParse(input,out var number)&&number>0;
        var name=input.TrimStart('@').ToLowerInvariant();
        if(!numeric&&!Regex.IsMatch(name,"^[a-z][a-z0-9_]{3,31}$",RegexOptions.CultureInvariant))
            throw new InvalidOperationException("آیدی معتبر مشتری را با یا بدون @ وارد کنید.");
        var customers=await db.Customers.AsNoTracking().Where(c=>!c.IsDeleted&&
            (numeric?c.TelegramId==input:c.Username!=null&&(c.Username.Trim().ToLower()==name||c.Username.Trim().ToLower()=="@"+name)))
            .Select(c=>new{c.Id,c.Username,c.TelegramId,c.FullName}).Take(2).ToArrayAsync(ct);
        if(customers.Length==0)return null;
        if(customers.Length>1)throw new InvalidOperationException("این آیدی به چند حساب مشتری متصل است؛ ابتدا حساب‌های تکراری بررسی شوند.");
        var customer=customers[0];
        var invoices=await db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&
            (i.Status==InvoiceStatus.Paid||i.Status==InvoiceStatus.Issued)&&i.Order!=null&&!i.Order.IsDeleted&&
            i.Order.Status!=OrderStatus.Cancelled&&i.Order.CustomerId==customer.Id)
            .Select(i=>new{i.Id,i.OrderId,i.Status,i.TotalAmount,Confirmed=i.Order!.Payments
                .Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed).Sum(p=>(decimal?)p.Amount)??0})
            .ToArrayAsync(ct);
        if(invoices.GroupBy(i=>i.OrderId).Any(g=>g.Count()>1))
            throw new InvalidOperationException("برای یک سفارش چند فاکتور فعال ثبت شده است؛ ابتدا حسابداری بررسی کند تا مبلغ دوباره محاسبه نشود.");
        var paid=invoices.Where(i=>i.Status==InvoiceStatus.Paid).ToArray();
        var pending=invoices.Where(i=>i.Status==InvoiceStatus.Issued).ToArray();
        var label=string.IsNullOrWhiteSpace(customer.Username)?customer.TelegramId??customer.FullName:"@"+customer.Username.Trim().TrimStart('@');
        return new(customer.Id,label,paid.Length,paid.Sum(i=>i.TotalAmount),pending.Length,pending.Sum(i=>i.TotalAmount),
            pending.Sum(i=>Math.Max(0,i.TotalAmount-i.Confirmed)),pending.Sum(i=>Math.Max(0,i.Confirmed)));
    }
}
