using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Interfaces;

public interface IShipmentRepository
{
    Task<Order?> GetOrderForShippingAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<Shipment?> GetByIdAsync(Guid shipmentId, CancellationToken cancellationToken = default);
    Task<Address?> GetAddressAsync(Guid addressId, CancellationToken cancellationToken = default);
    Task<bool> TrackingCodeExistsAsync(string trackingCode, CancellationToken cancellationToken = default);
    Task AddAsync(Shipment shipment, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
