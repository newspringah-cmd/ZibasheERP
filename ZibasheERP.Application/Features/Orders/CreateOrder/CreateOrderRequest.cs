using ZibasheERP.Domain.Enums;

namespace ZibasheERP.Application.Features.Orders.CreateOrder;

public class CreateOrderRequest
{
    public string TelegramId { get; set; } = string.Empty;

    public Guid SalesListId { get; set; }

    public int VolumeMl { get; set; }

    public BottleType BottleType { get; set; }

    public string? Note { get; set; }
}