using Microsoft.EntityFrameworkCore;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Services;

public sealed record RegistrationDebt(Guid? CustomerId,decimal OutstandingToman)
{
    public bool BlocksRegistration=>OutstandingToman>RegistrationDebtService.LimitToman;
}
public sealed class RegistrationDebtService(AppDbContext db)
{
    public const decimal LimitToman=10_000_000m;
    public const string CustomerMessage="به علت بدهی امکان ثبت آیتم ندارید؛ پس از پرداخت با حسابداری هماهنگ کنید لطفاً.";
    public async Task<RegistrationDebt> GetAsync(string userId,string? username,CancellationToken ct=default)
    {
        var numeric=long.TryParse(userId,out var id)&&id>0;
        var matches=numeric?await db.Customers.AsNoTracking().Where(c=>!c.IsDeleted&&c.TelegramId==userId).Select(c=>c.Id).ToArrayAsync(ct):[];
        if(matches.Length==0&&!string.IsNullOrWhiteSpace(username))
        {
            var name=username.Trim().TrimStart('@').ToLowerInvariant();
            matches=await db.Customers.AsNoTracking().Where(c=>!c.IsDeleted&&c.Username!=null&&
                (c.Username.Trim().ToLower()==name||c.Username.Trim().ToLower()=="@"+name)).Select(c=>c.Id).ToArrayAsync(ct);
        }
        if(matches.Length>1)throw new InvalidOperationException("اطلاعات مالی این آیدی یکتا نیست؛ لطفاً با حسابداری هماهنگ کنید.");
        if(matches.Length==0)return new(null,0);
        var customer=matches[0];
        var values=await db.Invoices.AsNoTracking().Where(i=>!i.IsDeleted&&i.Status==InvoiceStatus.Issued&&
            i.Order!=null&&!i.Order.IsDeleted&&i.Order.Status!=OrderStatus.Cancelled&&i.Order.CustomerId==customer)
            .Select(i=>new{i.TotalAmount,Paid=i.Order!.Payments.Where(p=>!p.IsDeleted&&p.Status==PaymentStatus.Confirmed)
                .Sum(p=>(decimal?)p.Amount)??0}).ToArrayAsync(ct);
        return new(customer,values.Sum(v=>Math.Max(0,v.TotalAmount-v.Paid)));
    }
}
