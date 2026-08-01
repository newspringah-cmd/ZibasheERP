namespace ZibasheERP.Domain.Entities;

public class Address : BaseEntity
{
    public Guid CustomerId { get; set; }

    public string ReceiverName { get; set; } = string.Empty;

    public string ReceiverMobile { get; set; } = string.Empty;

    public string Province { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    public string FullAddress { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public Customer Customer { get; set; } = null!;
}