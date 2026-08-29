using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Invoices;

public sealed record InvoiceResponse(
    Guid Id,
    Guid OrderId,
    string InvoiceNumber,
    string Status,
    string DeliveryStatus,
    DateTime IssuedAt,
    decimal PerfumeTotal,
    decimal BottleTotal,
    decimal TotalAmount,
    InvoiceCustomerResponse Customer,
    IReadOnlyCollection<InvoiceLineResponse> Items)
{
    public static InvoiceResponse FromEntity(Invoice invoice)
    {
        var order = invoice.Order
            ?? throw new InvalidOperationException("سفارش مرتبط با فاکتور بارگذاری نشده است.");
        var customer = order.Customer
            ?? throw new InvalidOperationException("مشتری مرتبط با فاکتور بارگذاری نشده است.");

        return new InvoiceResponse(
            invoice.Id,
            invoice.OrderId,
            invoice.InvoiceNumber,
            invoice.Status.ToString(),
            invoice.DeliveryStatus.ToString(),
            invoice.IssuedAt,
            invoice.PerfumeTotal,
            invoice.BottleTotal,
            invoice.TotalAmount,
            new InvoiceCustomerResponse(
                customer.Id,
                customer.FullName,
                customer.Mobile,
                customer.TelegramId),
            order.Items
                .OrderBy(item => item.RowNumber)
                .Select(item => new InvoiceLineResponse(
                    item.Id,
                    item.Perfume?.Name ?? item.ManualDescription ?? string.Empty,
                    item.Perfume?.Brand ?? string.Empty,
                    item.RequestedVolumeMl,
                    item.PerfumePricePerMl,
                    item.PerfumeAmount,
                    item.IsBottleOwner,
                    item.Bottle?.Name,
                    item.BottlePrice,
                    item.LineTotal))
                .ToArray());
    }
}

public sealed record InvoiceCustomerResponse(
    Guid Id,
    string FullName,
    string Mobile,
    string? TelegramId);

public sealed record InvoiceLineResponse(
    Guid Id,
    string PerfumeName,
    string PerfumeBrand,
    int VolumeMl,
    decimal PricePerMl,
    decimal PerfumeAmount,
    bool IsBottleOwner,
    string? BottleName,
    decimal BottlePrice,
    decimal LineTotal);
