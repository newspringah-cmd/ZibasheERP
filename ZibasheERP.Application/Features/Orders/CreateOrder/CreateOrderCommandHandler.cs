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
    private readonly IBottleRepository _bottleRepository;
    private readonly IBatchRepository _batchRepository;

    public CreateOrderCommandHandler(
        ICustomerRepository customerRepository,
        IOrderRepository orderRepository,
        ISalesListRepository salesListRepository,
        IBottleRepository bottleRepository,
        IBatchRepository batchRepository)
    {
        _customerRepository = customerRepository;
        _orderRepository = orderRepository;
        _salesListRepository = salesListRepository;
        _bottleRepository = bottleRepository;
        _batchRepository = batchRepository;
    }

    public async Task<Guid> Handle(
        CreateOrderCommand request,
        CancellationToken cancellationToken)

    {
        if (!string.IsNullOrWhiteSpace(request.ExternalReference))
        {
            var existingOrder = await _orderRepository.GetByExternalReferenceAsync(
                request.ExternalReference.Trim(),
                cancellationToken);
            if (existingOrder is not null)
                return existingOrder.Id;
        }

        var customer = request.CustomerId != Guid.Empty
            ? await _customerRepository.GetByIdAsync(
                request.CustomerId,
                cancellationToken)
            : await _customerRepository.GetByTelegramIdAsync(
                NormalizeTelegramId(request.TelegramId!),
                cancellationToken);

        if (customer is null)
            throw new InvalidOperationException("مشتری پیدا نشد.");

        if (customer.IsBlocked)
            throw new InvalidOperationException("این مشتری مسدود شده است.");

        if (!customer.CanPlaceOrder)
            throw new InvalidOperationException(
                "امکان ثبت سفارش برای این مشتری غیرفعال است.");

        var salesList = await _salesListRepository.GetByIdAsync(
            request.SalesListId,
            cancellationToken);

        if (salesList is null)
            throw new InvalidOperationException("لیست فروش پیدا نشد.");

        if (salesList.Status != SalesListStatus.Open)
            throw new InvalidOperationException(
                "این لیست دیگر امکان ثبت سفارش ندارد.");

        if (request.RequestedVolumeMl <= 0)
            throw new InvalidOperationException(
                "حجم درخواستی باید بیشتر از صفر باشد.");

        if (request.RequestedVolumeMl > salesList.RemainingVolume)
            throw new InvalidOperationException(
                $"حجم کافی موجود نیست. حجم باقی‌مانده لیست {salesList.RemainingVolume} میل است.");

        if (!salesList.BatchId.HasValue)
            throw new InvalidOperationException(
                "برای این لیست هنوز عطر خریداری نشده و بچ واقعی به آن متصل نیست؛ ابتدا خرید عطر را ثبت کنید.");

        var batch = await _batchRepository.GetByIdAsync(
            salesList.BatchId.Value,
            cancellationToken);

        if (batch is null)
            throw new InvalidOperationException(
                "بچ مرتبط با لیست فروش پیدا نشد.");

        if (batch.Perfume is null)
            throw new InvalidOperationException(
                "عطر مرتبط با بچ پیدا نشد.");

        if (!batch.Perfume.IsActive)
            throw new InvalidOperationException(
                "عطر این لیست غیرفعال است.");

        var isBottleOwner = ResolveBottleOwner(
            request,
            customer,
            salesList);

        Bottle? selectedBottle = null;
        decimal bottlePrice = 0;

        if (!isBottleOwner)
        {
            if (!request.BottleId.HasValue)
                throw new InvalidOperationException(
                    "برای سفارش عادی باید شیشه انتخاب شود.");

            selectedBottle = await _bottleRepository.GetByIdAsync(
                request.BottleId.Value,
                cancellationToken);

            if (selectedBottle is null)
                throw new InvalidOperationException(
                    "شیشه انتخاب‌شده پیدا نشد یا غیرفعال است.");

            ValidateBottle(
                selectedBottle,
                request.RequestedVolumeMl);

            bottlePrice = selectedBottle.SalePrice;
        }
        else if (request.BottleId.HasValue)
        {
            throw new InvalidOperationException(
                "برای صاحب باتل نباید شیشه دکانت انتخاب شود.");
        }

        var perfumePricePerMl = salesList.PricePerMl;

        if (perfumePricePerMl <= 0)
            throw new InvalidOperationException(
                "قیمت هر میل در لیست فروش معتبر نیست.");

        var perfumeAmount =
            perfumePricePerMl * request.RequestedVolumeMl;

        var lineTotal =
            perfumeAmount + bottlePrice;

        if (customer.AvailableCredit < lineTotal)
        {
            throw new InvalidOperationException(
                $"اعتبار مشتری کافی نیست. اعتبار قابل استفاده: " +
                $"{customer.AvailableCredit:N0}، مبلغ سفارش: {lineTotal:N0}");
        }

        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();

        var orderNumber = await GenerateOrderNumberAsync(
            now,
            cancellationToken);

        var order = new Order
        {
            Id = orderId,
            CreatedAt = now,
            CustomerId = customer.Id,
            SalesListId = salesList.Id,
            OrderNumber = orderNumber,
            ExternalReference = string.IsNullOrWhiteSpace(request.ExternalReference)
                ? null
                : request.ExternalReference.Trim(),
            Status = OrderStatus.Registered,
            RegisteredAt = now,
            PerfumeTotal = perfumeAmount,
            BottleTotal = bottlePrice,
            FinalAmount = lineTotal,
            Notes = NormalizeNotes(request.Notes)
        };

        var orderItem = new OrderItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            OrderId = orderId,
            SalesListId = salesList.Id,
            PerfumeId = batch.PerfumeId,
            RequestedVolumeMl = request.RequestedVolumeMl,
            Quantity = 1,
            PerfumePricePerMl = perfumePricePerMl,
            PerfumeAmount = perfumeAmount,
            IsBottleOwner = isBottleOwner,
            BottleId = selectedBottle?.Id,
            BottlePrice = bottlePrice,
            LineTotal = lineTotal,
            RowNumber = CalculateRowNumber(salesList),
            Notes = NormalizeNotes(request.Notes)
        };

        order.Items.Add(orderItem);

        salesList.ReservedVolume += request.RequestedVolumeMl;
        salesList.UpdatedAt = now;

        if (isBottleOwner)
        {
            salesList.HasBottleOwner = true;
            salesList.BottleOwnerCustomerId = customer.Id;
        }

        if (salesList.ReservedVolume == salesList.TotalVolume)
        {
            salesList.Status = SalesListStatus.Full;
            salesList.ClosedDate = now;
            order.Status = OrderStatus.ListCompleted;
        }

        customer.CurrentDebt += lineTotal;
        customer.LastOrderAt = now;
        customer.UpdatedAt = now;

        await _orderRepository.AddAsync(
            order,
            cancellationToken);

        await _salesListRepository.UpdateAsync(
            salesList,
            cancellationToken);

        await _customerRepository.UpdateAsync(
            customer,
            cancellationToken);

        /*
         * همه Repositoryها از یک AppDbContext Scoped استفاده می‌کنند.
         * یک SaveChanges همه تغییرات Order، OrderItem، Customer و SalesList
         * را در یک تراکنش دیتابیس ذخیره می‌کند.
         */
        await _orderRepository.SaveChangesAsync(
            cancellationToken);

        return order.Id;
    }

    private static bool ResolveBottleOwner(
        CreateOrderCommand request,
        Customer customer,
        SalesList salesList)
    {
        if (request.IsBottleOwner)
        {
            if (salesList.HasBottleOwner ||
                salesList.BottleOwnerCustomerId.HasValue)
            {
                throw new InvalidOperationException(
                    "صاحب باتل این لیست قبلاً مشخص شده است.");
            }

            return true;
        }

        if (salesList.BottleOwnerCustomerId == customer.Id)
            return true;

        return false;
    }

    private static void ValidateBottle(
        Bottle bottle,
        int requestedVolumeMl)
    {
        if (bottle.VolumeMl != requestedVolumeMl)
        {
            throw new InvalidOperationException(
                $"حجم شیشه انتخاب‌شده {bottle.VolumeMl} میل است، " +
                $"اما حجم سفارش {requestedVolumeMl} میل ثبت شده است.");
        }

        if (requestedVolumeMl == 3 &&
            bottle.Type != BottleType.Normal)
        {
            throw new InvalidOperationException(
                "برای حجم ۳ میل فقط شیشه معمولی مجاز است.");
        }

        if (requestedVolumeMl > 10 &&
            bottle.Type != BottleType.Fancy)
        {
            throw new InvalidOperationException(
                "برای حجم بیشتر از ۱۰ میل باید شیشه فانتزی انتخاب شود.");
        }
    }

    private async Task<string> GenerateOrderNumberAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var randomPart = Random.Shared.Next(1000, 10000);

            var orderNumber =
                $"ZS-{now:yyyyMMddHHmmss}-{randomPart}";

            var exists =
                await _orderRepository.OrderNumberExistsAsync(
                    orderNumber,
                    cancellationToken);

            if (!exists)
                return orderNumber;
        }

        throw new InvalidOperationException(
            "تولید شماره سفارش یکتا ناموفق بود. دوباره تلاش کنید.");
    }

    private static int CalculateRowNumber(
        SalesList salesList)
    {
        /*
         * فعلاً ترتیب تقریبی بر اساس حجم رزروشده ثبت می‌شود.
         * بعداً برای شماره ردیف دقیق، یک Query اختصاصی
         * روی OrderItemهای همان SalesList اضافه می‌کنیم.
         */
        return salesList.ReservedVolume + 1;
    }

    private static string? NormalizeNotes(
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;

        var normalized = notes.Trim();

        return normalized.Length <= 500
            ? normalized
            : normalized[..500];
    }

    private static string NormalizeTelegramId(string telegramId)
    {
        return telegramId.Trim();
    }
}
