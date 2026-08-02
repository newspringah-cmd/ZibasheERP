using System.ComponentModel.DataAnnotations;

namespace ZibasheERP.Domain.Entities;

public class Address : BaseEntity
{
    public Guid CustomerId { get; set; }

    public Customer? Customer { get; set; }

    [Required]
    [MaxLength(100)]
    public string ReceiverName { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Mobile { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Province { get; set; } = string.Empty;

    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [MaxLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string FullAddress { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Description { get; set; }

    public bool IsDefault { get; set; }
}