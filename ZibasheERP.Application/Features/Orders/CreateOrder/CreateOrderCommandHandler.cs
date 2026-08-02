using MediatR;
using ZibasheERP.Application.Interfaces;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.Application.Features.Orders.CreateOrder;

public class CreateOrderCommandHandler
    : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ISalesListRepository _salesListRepository;

    public CreateOrderCommandHandler(
        ICustomerRepository customerRepository,
        IOrderRepository orderRepository,
        ISalesListRepository salesListRepository)
    {
        _customerRepository = customerRepository;
        _orderRepository = orderRepository;
        _salesListRepository = salesListRepository;
    }

    public async Task<Guid> Handle(
        CreateOrderCommand request,
        CancellationToken cancellationToken)
    {
        // 1- پیدا کردن مشتری
        var customer = await _customerRepository.GetByIdAsync(
            request.CustomerId,
            cancellationToken);

        if (customer is null)
            throw new Exception("مشتری پیدا نشد.");

        // 2- بررسی وضعیت مشتری
        if (customer.IsBlocked)
            throw new Exception("این مشتری مسدود شده است.");

        if (!customer.CanPlaceOrder)
            throw new Exception("این مشتری امکان ثبت سفارش ندارد.");

        // 3- پیدا کردن لیست فروش
        var salesList = await _salesListRepository.GetByIdAsync(
            request.SalesListId,
            cancellationToken);

        if (salesList is null)
            throw new Exception("لیست فروش پیدا نشد.");

        // 4- فقط لیست باز قابل سفارش است
        if (salesList.Status != SalesListStatus.Open)
            throw new Exception("این لیست دیگر امکان ثبت سفارش ندارد.");

        // 5- بررسی حجم باقیمانده
        if (salesList.RemainingVolume < request.RequestedVolumeMl)
            throw new Exception("حجم کافی در لیست فروش موجود نیست.");

        // مرحله بعد:
        // ایجاد Order و OrderItem

        throw new NotImplementedException();
    }
}