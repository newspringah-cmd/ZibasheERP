using System.ComponentModel.DataAnnotations;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Contracts.Customers;

public sealed class CreateCustomerRequest
{
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [Phone]
    [MaxLength(20)]
    public string Mobile { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TelegramId { get; set; }

    [MaxLength(100)]
    public string? Username { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal CreditLimit { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999")]
    public decimal WalletBalance { get; set; }
}

public sealed class UpdateCustomerRequest
{
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [Phone]
    [MaxLength(20)]
    public string Mobile { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TelegramId { get; set; }

    [MaxLength(100)]
    public string? Username { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}

public sealed record CustomerResponse(
    Guid Id,
    string FullName,
    string Mobile,
    string? TelegramId,
    string? Username,
    DateTime CreatedAt)
{
    public static CustomerResponse FromEntity(Customer customer) => new(
        customer.Id,
        customer.FullName,
        customer.Mobile,
        customer.TelegramId,
        customer.Username,
        customer.CreatedAt);
}
