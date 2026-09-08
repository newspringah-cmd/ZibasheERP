namespace ZibasheERP.Domain.Entities;

public enum OrderItemFulfillmentStatus
{
    WaitingForListCompletion = 1,
    ListCompleted = 2,
    AwaitingPurchase = 3,
    Purchased = 4,
    Invoiced = 5,
    WaitingForArrivalInIran = 6,
    ArrivedInIran = 7,
    DecantQueue = 8,
    DecantedReadyToShip = 9,
    Shipped = 10
}

public class OrderItem : BaseEntity
{
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    // لیست فروشی که این آیتم از آن ثبت شده است
    public Guid? SalesListId { get; set; }

    public SalesList? SalesList { get; set; }

    public Guid? SourceSalesListRequestId { get; set; }

    public SalesListRequest? SourceSalesListRequest { get; set; }

    public Guid? PerfumeId { get; set; }

    public Perfume? Perfume { get; set; }

    public string? ManualDescription { get; set; }

    // حجم درخواستی
    public int RequestedVolumeMl { get; set; }

    // تعداد (فعلاً همیشه 1 است اما برای آینده نگه می‌داریم)
    public int Quantity { get; set; } = 1;

    // قیمت هر میل در زمان صدور فاکتور
    public decimal PerfumePricePerMl { get; set; }

    // مبلغ عطر
    public decimal PerfumeAmount { get; set; }

    

    // صاحب باتل این لیست است؟
    public bool IsBottleOwner { get; set; }

    // نوع شیشه انتخاب‌شده
    public Guid? BottleId { get; set; }

    public Bottle? Bottle { get; set; }

    // قیمت شیشه در زمان صدور فاکتور
    public decimal BottlePrice { get; set; }

    // مبلغ نهایی این آیتم
    public decimal LineTotal { get; set; }

    // ترتیب ثبت داخل لیست (بعداً برای گزارش‌ها مفید است)
    public int RowNumber { get; set; }

    public OrderItemFulfillmentStatus FulfillmentStatus { get; set; }
        = OrderItemFulfillmentStatus.WaitingForListCompletion;

    public DateTime? ArrivedInIranAt { get; set; }
    public DateTime? EnteredDecantQueueAt { get; set; }
    public DateTime? DecantedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public Guid? ShippingRequestId { get; set; }
    public DateTime? ShippingRequestedAt { get; set; }

    public string? Notes { get; set; }
}
