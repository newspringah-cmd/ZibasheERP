using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly AppDbContext _dbContext;

    public InvoiceRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        InvoiceQuery().FirstOrDefaultAsync(
            invoice => invoice.Id == id && !invoice.IsDeleted,
            cancellationToken);

    public Task<Invoice?> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        InvoiceQuery().FirstOrDefaultAsync(
            invoice => invoice.OrderId == orderId && !invoice.IsDeleted,
            cancellationToken);

    public Task<Order?> GetOrderForInvoiceAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Orders
            .Include(order => order.Customer)
            .Include(order => order.Items)
                .ThenInclude(item => item.Perfume)
            .Include(order => order.Items)
                .ThenInclude(item => item.Bottle)
            .FirstOrDefaultAsync(
                order => order.Id == orderId && !order.IsDeleted,
                cancellationToken);
    }

    public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
        _dbContext.Invoices.AnyAsync(
            invoice => invoice.InvoiceNumber == invoiceNumber,
            cancellationToken);

    public async Task AddAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        await _dbContext.Invoices.AddAsync(invoice, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private IQueryable<Invoice> InvoiceQuery() => _dbContext.Invoices
        .AsNoTracking()
        .Include(invoice => invoice.Order)
            .ThenInclude(order => order!.Customer)
        .Include(invoice => invoice.Order)
            .ThenInclude(order => order!.Items)
                .ThenInclude(item => item.Perfume)
        .Include(invoice => invoice.Order)
            .ThenInclude(order => order!.Items)
                .ThenInclude(item => item.Bottle);
}
