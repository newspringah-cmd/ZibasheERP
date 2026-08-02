using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Application.Notifications;
using ZibasheERP.Domain.Entities;
using ZibasheERP.Domain.Enums;
using OrderState = ZibasheERP.Domain.Entities.OrderStatus;

namespace ZibasheERP.Application.Features.Orders.CancelOrder;

public sealed class CancelOrderCommandHandler
    : IRequestHandler<CancelOrderCommand, CancelOrderResponse>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly INotificationOutboxRepository _outboxRepository;

    public CancelOrderCommandHandler(
        IOrderRepository orderRepository,
        IInvoiceRepository invoiceRepository,
        INotificationOutboxRepository outboxRepository)
    {
        _orderRepository = orderRepository;
        _invoiceRepository = invoiceRepository;
        _outboxRepository = outboxRepository;
    }

    public async Task<CancelOrderResponse> Handle(
        CancelOrderCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetForUpdateAsync(request.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("سفارش پیدا نشد.");
        var reason = request.Reason.Trim();
        if (reason.Length is < 3 or > 500)
            throw new InvalidOperationException("علت لغو باید بین ۳ تا ۵۰۰ کاراکتر باشد.");

        if (order.Status == OrderState.Cancelled && order.CancelledAt.HasValue)
            return new(order.Id, order.Status.ToString(), order.CancelReason ?? reason, order.CancelledAt.Value);
        if (order.Status is OrderState.Decanted or OrderState.ReadyToShip or OrderState.Shipped or OrderState.Delivered)
            throw new InvalidOperationException("سفارش ارسال‌شده یا تحویل‌شده قابل لغو نیست.");
        if (order.Payments.Any(payment =>
                !payment.IsDeleted && payment.Status == PaymentStatus.Confirmed))
        {
            throw new InvalidOperationException(
                "سفارش دارای پرداخت تأییدشده است؛ ابتدا باید فرایند بازپرداخت انجام شود.");
        }
        var invoice = await _invoiceRepository.GetForUpdateByOrderIdAsync(
            order.Id,
            cancellationToken);
        if (invoice?.Status == InvoiceStatus.Paid)
            throw new InvalidOperationException("فاکتور پرداخت‌شده بدون بازپرداخت قابل لغو نیست.");

        var customer = order.Customer
            ?? throw new InvalidOperationException("مشتری سفارش بارگذاری نشده است.");
        var salesList = order.SalesList
            ?? throw new InvalidOperationException("لیست فروش سفارش بارگذاری نشده است.");
        var now = DateTime.UtcNow;
        var reservedVolume = order.Items
            .Where(item => !item.IsDeleted)
            .Sum(item => item.RequestedVolumeMl);

        salesList.ReservedVolume = Math.Max(0, salesList.ReservedVolume - reservedVolume);
        if (salesList.Status == ZibasheERP.Domain.Entities.SalesListStatus.Full)
        {
            salesList.Status = ZibasheERP.Domain.Entities.SalesListStatus.Open;
            salesList.ClosedDate = null;
        }
        if (order.Items.Any(item => item.IsBottleOwner) &&
            salesList.BottleOwnerCustomerId == order.CustomerId)
        {
            salesList.HasBottleOwner = false;
            salesList.BottleOwnerCustomerId = null;
        }
        salesList.UpdatedAt = now;

        customer.CurrentDebt = Math.Max(0, customer.CurrentDebt - order.FinalAmount);
        customer.UpdatedAt = now;
        foreach (var payment in order.Payments.Where(payment => payment.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.UpdatedAt = now;
        }

        if (invoice is not null && invoice.Status != InvoiceStatus.Paid)
        {
            invoice.Status = InvoiceStatus.Cancelled;
            invoice.UpdatedAt = now;
        }

        order.Status = OrderState.Cancelled;
        order.CancelledAt = now;
        order.CancelReason = reason;
        order.UpdatedAt = now;

        var notification = TelegramNotificationFactory.Create(
            order,
            "OrderCancelled",
            new { order.Id, order.OrderNumber, Reason = reason },
            now);
        if (notification is not null)
            await _outboxRepository.AddAsync(notification, cancellationToken);

        await _orderRepository.SaveChangesAsync(cancellationToken);
        return new(order.Id, order.Status.ToString(), reason, now);
    }
}
