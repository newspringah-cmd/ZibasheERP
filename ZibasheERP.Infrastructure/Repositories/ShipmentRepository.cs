using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Infrastructure.Persistence;

namespace ZibasheERP.Infrastructure.Repositories;

public sealed class ShipmentRepository : IShipmentRepository
{
    private readonly AppDbContext _dbContext;

    public ShipmentRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Order?> GetOrderForShippingAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Orders
            .Include(order => order.Customer)
                .ThenInclude(customer => customer!.TelegramGroup)
            .Include(order => order.Shipments)
            .FirstOrDefaultAsync(
                order => order.Id == orderId && !order.IsDeleted,
                cancellationToken);
    }

    public Task<Address?> GetAddressAsync(Guid addressId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Addresses.FirstOrDefaultAsync(
            address => address.Id == addressId && !address.IsDeleted,
            cancellationToken);
    }

    public Task<Shipment?> GetByIdAsync(
        Guid shipmentId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.Shipments
            .Include(shipment => shipment.Order)
                .ThenInclude(order => order!.Customer)
                    .ThenInclude(customer => customer!.TelegramGroup)
            .FirstOrDefaultAsync(
                shipment => shipment.Id == shipmentId && !shipment.IsDeleted,
                cancellationToken);
    }

    public Task<bool> TrackingCodeExistsAsync(string trackingCode, CancellationToken cancellationToken = default)
    {
        return _dbContext.Shipments.AnyAsync(
            shipment => shipment.TrackingCode == trackingCode,
            cancellationToken);
    }

    public async Task AddAsync(Shipment shipment, CancellationToken cancellationToken = default)
    {
        await _dbContext.Shipments.AddAsync(shipment, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
