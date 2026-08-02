namespace ZibasheERP.Application.Features.Orders.GetOrder;

public sealed record GetOrderResponse(
    Guid Id,
    string OrderNumber,
    string Status,
    DateTime RegisteredAt,
    decimal PerfumeTotal,
    decimal BottleTotal,
    decimal FinalAmount,
    Guid? DeliveryAddressId,
    string? Notes,
    OrderCustomerResponse Customer,
    IReadOnlyCollection<GetOrderItemResponse> Items);

public sealed record OrderCustomerResponse(
    Guid Id,
    string FullName,
    string Mobile,
    string? TelegramId);

public sealed record GetOrderItemResponse(
    Guid Id,
    Guid SalesListId,
    string PerfumeName,
    string PerfumeBrand,
    int RequestedVolumeMl,
    decimal PerfumePricePerMl,
    decimal PerfumeAmount,
    bool IsBottleOwner,
    string? BottleName,
    decimal BottlePrice,
    decimal LineTotal,
    int RowNumber,
    string? Notes);
